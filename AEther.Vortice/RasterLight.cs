using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Point-lit procedural geometry and homogeneous single scattering.
internal sealed class RasterLight : IDisposable
{
    internal const int Capacity = 512;
    internal sealed class Programs : IDisposable
    {
        readonly List<IDisposable> owned = [];
        internal readonly ShaderProgram Lights, Surface, Airlight, LookupPrepare, LookupBuild, LookupCommit;
        internal readonly RasterGeometry.Programs Geometry;
        internal Programs(ID3D11Device device, string source, string file)
        {
            try
            {
                LookupPrepare = Own(new ShaderProgram(device, source, "PrepareAirlightLookup", file, "cs_5_0"));
                LookupBuild = Own(new ShaderProgram(device, source, "BuildAirlightLookup", file, "cs_5_0"));
                LookupCommit = Own(new ShaderProgram(device, source, "CommitAirlightLookup", file, "cs_5_0"));
                Lights = Own(new ShaderProgram(device, source, "PreparePointLights", file, "cs_5_0"));
                Surface = Own(new ShaderProgram(device, source, "RasterSurfacePS", file));
                Airlight = Own(new ShaderProgram(device, source, "RasterAirlightPS", file));
                Geometry = Own(new RasterGeometry.Programs(device, source, file));
            }
            catch { Dispose(); throw; }
        }
        T Own<T>(T resource) where T : IDisposable { owned.Add(resource); return resource; }
        public void Dispose() { for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); owned.Clear(); }
    }

    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly AirlightLookup lookup;
    readonly List<IDisposable> owned = [];
    readonly RasterGeometry geometry;
    readonly ID3D11DepthStencilState depthState;
    readonly Dictionary<string, ID3D11UnorderedAccessView> outputs = [], lookupOutputs = [];
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = [];
    readonly Dictionary<string, ID3D11SamplerState> samplers = [];
    readonly ID3D11ShaderResourceView lightsView, countView;
    readonly Dictionary<string, ID3D11Buffer> passBuffers = [];
    Target? surface, depth, air;
    Programs? lookupPrograms;
    internal Action<string, bool>? PassMarker { get; set; }
    internal ID3D11SamplerState LookupSampler => lookup.Sampler;

    internal RasterLight(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device = device; this.context = context;
        try
        {
            lookup = Own(new AirlightLookup(device, context));
            geometry = Own(new RasterGeometry(device));
            depthState = Own(device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true, DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.LessEqual, StencilEnable = false,
            }));
            var lights = Own(device.CreateBuffer(new BufferDescription
            {
                ByteWidth = Capacity * 24, StructureByteStride = 24, Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.BufferStructured,
            }));
            outputs["PointLightOutput"] = Own(device.CreateUnorderedAccessView(lights, new UnorderedAccessViewDescription
            {
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { NumElements = Capacity },
            }));
            lightsView = Own(device.CreateShaderResourceView(lights));
            var count = Own(device.CreateBuffer(new BufferDescription
            {
                ByteWidth = 16, Usage = ResourceUsage.Default, BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.BufferAllowRawViews,
            }));
            outputs["PointLightCountOutput"] = Own(device.CreateUnorderedAccessView(count, new UnorderedAccessViewDescription
            {
                Format = Format.R32_Typeless, ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView { NumElements = 4, Flags = BufferUnorderedAccessViewFlags.Raw },
            }));
            countView = Own(device.CreateShaderResourceView(count, new ShaderResourceViewDescription
            {
                Format = Format.R32_Typeless, ViewDimension = ShaderResourceViewDimension.BufferExtended,
                BufferEx = new BufferExtendedShaderResourceView { NumElements = 4, Flags = BufferExtendedShaderResourceViewFlags.Raw },
            }));
        }
        catch { Dispose(); throw; }
    }
    T Own<T>(T resource) where T : IDisposable { owned.Add(resource); return resource; }

    internal void Prepare(Programs program, ShaderParameters? p, int width, int height,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IDictionary<string, ID3D11ShaderResourceView> sceneTextures,
        IReadOnlyDictionary<string, ID3D11SamplerState> sceneSamplers)
    {
        passBuffers.Clear(); foreach (var pair in buffers) passBuffers[pair.Key] = pair.Value;
        if (surface?.Width != width || surface.Height != height)
        {
            surface?.Dispose(); surface = null; depth?.Dispose(); depth = null;
            surface = new(device, width, height); depth = new(device, width, height, isDepth: true);
        }
        if (air?.Width != width || air.Height != height)
        { air?.Dispose(); air = null; air = new(device, width, height); }

        textures.Clear(); foreach (var pair in sceneTextures) textures[pair.Key] = pair.Value;
        textures["PointLights"] = lightsView; textures["PointLightCount"] = countView;
        textures["RasterAirlightLut"] = lookup.View;
        textures["RasterLookupState"] = lookup.State;
        samplers.Clear(); foreach (var pair in sceneSamplers) samplers[pair.Key] = pair.Value;
        samplers["RasterLutSampler"] = lookup.Sampler;
        // Previous frame SRVs must be released before compute writes or target binding.
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        if (!ReferenceEquals(lookupPrograms, program))
        {
            lookup.Invalidate(context);
            lookupPrograms = program;
        }
        Mark("Raster lookup", true);
        lookupOutputs.Clear(); lookupOutputs["RasterLookupOutputState"] = lookup.StateOutput;
        program.LookupPrepare.Bind(context, p, passBuffers, textures, samplers, lookupOutputs);
        context.Dispatch(1, 1, 1);
        context.CSSetUnorderedAccessView(0, null);
        lookupOutputs.Clear(); lookupOutputs["RasterLookupOutput"] = lookup.Output;
        program.LookupBuild.Bind(context, p, passBuffers, textures, samplers, lookupOutputs);
        context.Dispatch((AirlightLookup.Width + 7) / 8, (AirlightLookup.Height + 7) / 8, 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
        lookupOutputs.Clear(); lookupOutputs["RasterLookupOutputState"] = lookup.StateOutput;
        program.LookupCommit.Bind(context, p, passBuffers, textures, samplers, lookupOutputs);
        context.Dispatch(1, 1, 1);
        context.CSSetUnorderedAccessView(0, null);
        Mark("Raster lookup", false);
        Mark("Raster lights prepare", true);
        program.Lights.Bind(context, p, passBuffers, textures, samplers, outputs);
        context.Dispatch((Capacity + 63) / 64, 1, 1);
        context.CSSetUnorderedAccessView(0, null); context.CSSetUnorderedAccessView(1, null);
        context.CSSetShaderResources(0, ShaderProgram.EmptyViews); context.CSSetShader(null);
        Mark("Raster lights prepare", false);
        Mark("Raster geometry prepare", true);
        geometry.Prepare(context, program.Geometry, p, passBuffers, textures, samplers);
        Mark("Raster geometry prepare", false);
        program.Geometry.Vertex.Bind(context, p, passBuffers, textures, samplers);
        program.Surface.Bind(context, p, passBuffers, textures, samplers);
        context.OMSetBlendState(null); context.OMSetDepthStencilState(depthState);
        context.RSSetViewport(0, 0, width, height);
        context.ClearRenderTargetView(surface.Color!, new(0, 0, 0, 0));
        context.ClearDepthStencilView(depth!.Depth!, DepthStencilClearFlags.Depth, 1, 0);
        context.OMSetRenderTargets(surface.Color!, depth.Depth);
        Mark("Raster surface", true); geometry.Draw(context); Mark("Raster surface", false);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.OMSetDepthStencilState(null);
        textures["RasterDepth"] = depth.View;
        sceneTextures["RasterSurface"] = surface.View; sceneTextures["RasterDepth"] = depth.View;
        sceneTextures["RasterAirlight"] = air.View; sceneTextures["PointLights"] = lightsView;
        sceneTextures["PointLightCount"] = countView; sceneTextures["RasterAirlightLut"] = lookup.View; sceneTextures["RasterLookupState"] = lookup.State;
    }
    internal void IntegrateAirlight(Programs program, ShaderParameters? p)
    {
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(air!.Color!);
        context.RSSetViewport(0, 0, air.Width, air.Height);
        program.Airlight.Bind(context, p, passBuffers, textures, samplers);
        Mark("Raster airlight", true); context.Draw(3, 0); Mark("Raster airlight", false);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.RSSetViewport(0, 0, surface!.Width, surface.Height);
    }
    void Mark(string name, bool begin) => PassMarker?.Invoke(name, begin);

    // Camera surface/depth and airlight targets.
    sealed class Target : IDisposable
    {
        readonly ID3D11Texture2D texture;
        internal readonly ID3D11RenderTargetView? Color;
        internal readonly ID3D11DepthStencilView? Depth;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly int Width, Height;
        internal Target(ID3D11Device device, int width, int height, bool isDepth = false)
        {
            Width = width; Height = height;
            texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, ArraySize = 1, MipLevels = 1,
                Format = isDepth ? Format.R32_Typeless : Format.R32G32B32A32_Float,
                SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | (isDepth ? BindFlags.DepthStencil : BindFlags.RenderTarget),
            });
            try
            {
                if (isDepth) Depth = device.CreateDepthStencilView(texture, new DepthStencilViewDescription
                    { Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D });
                else Color = device.CreateRenderTargetView(texture);
                View = device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
                {
                    Format = isDepth ? Format.R32_Float : Format.R32G32B32A32_Float,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1 },
                });
            }
            catch { Color?.Dispose(); Depth?.Dispose(); texture.Dispose(); throw; }
        }
        public void Dispose() { View.Dispose(); Color?.Dispose(); Depth?.Dispose(); texture.Dispose(); }
    }
    public void Dispose()
    {
        surface?.Dispose(); depth?.Dispose(); air?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); owned.Clear();
    }
}

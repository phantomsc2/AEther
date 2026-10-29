using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Point-lit procedural geometry and homogeneous single scattering.
internal sealed class RasterLight : IDisposable
{

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
        internal IEnumerable<ShaderParameters?> ParameterLayouts => owned.OfType<ShaderProgram>().Select(p => p.Parameters).Append(Geometry.Vertex.Parameters);
        T Own<T>(T resource) where T : IDisposable { owned.Add(resource); return resource; }
        public void Dispose() { for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); owned.Clear(); }
    }

    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    AirlightLookup? lookup;
    readonly List<IDisposable> owned = [];

    readonly ID3D11DepthStencilState depthState;
    readonly Dictionary<string, ID3D11UnorderedAccessView> outputs = [], lookupOutputs = [];
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = [];
    readonly Dictionary<string, ID3D11SamplerState> samplers = [];
    LightBuffer? lights;
    internal ID3D11ShaderResourceView LightsView => lights!.View;
    internal uint LightCount { get; private set; }
    readonly Dictionary<string, ID3D11Buffer> passBuffers = [];
    Target? surface, depth, air;
    Programs? lookupPrograms;
    internal Action<string, bool>? PassMarker { get; set; }
    internal ID3D11SamplerState LookupSampler => lookup!.Sampler;
    internal SceneSizes AllocatedSizes { get; private set; }

    internal RasterLight(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device = device; this.context = context;
        try
        {
            depthState = Own(device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true, DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.LessEqual, StencilEnable = false,
            }));

        }
        catch { Dispose(); throw; }
    }
    T Own<T>(T resource) where T : IDisposable { owned.Add(resource); return resource; }

    internal void Configure(SceneSizes sizes)
    {
        if (lights != null && sizes == AllocatedSizes) return;
        LightBuffer? nextLights = null;
        AirlightLookup? nextLookup = null;
        Target? nextSurface = null, nextDepth = null, nextAir = null;
        try
        {
            if (lights == null || LightCount != sizes.PointLights) nextLights = new(device, sizes.PointLights);
            if (lookup == null || lookup.Width != sizes.LookupWidth || lookup.Height != sizes.LookupHeight)
                nextLookup = new(device, context, sizes.LookupWidth, sizes.LookupHeight);
            if (surface == null || surface.Width != sizes.SurfaceWidth || surface.Height != sizes.SurfaceHeight)
            {
                nextSurface = new(device, sizes.SurfaceWidth, sizes.SurfaceHeight);
                nextDepth = new(device, sizes.SurfaceWidth, sizes.SurfaceHeight, isDepth: true);
            }
            if (air == null || air.Width != sizes.AirWidth || air.Height != sizes.AirHeight)
                nextAir = new(device, sizes.AirWidth, sizes.AirHeight);
        }
        catch
        {
            nextLights?.Dispose(); nextLookup?.Dispose(); nextSurface?.Dispose(); nextDepth?.Dispose(); nextAir?.Dispose();
            throw;
        }
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
        if (nextLights != null) { lights?.Dispose(); lights = nextLights; }
        if (nextLookup != null) { lookup?.Dispose(); lookup = nextLookup; lookupPrograms = null; }
        if (nextSurface != null) { surface?.Dispose(); depth?.Dispose(); surface = nextSurface; depth = nextDepth; }
        if (nextAir != null) { air?.Dispose(); air = nextAir; }
        LightCount = sizes.PointLights;
        AllocatedSizes = sizes;
        outputs["PointLightOutput"] = lights!.Output;
    }

    sealed class LightBuffer : IDisposable
    {
        readonly ID3D11Buffer buffer;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11UnorderedAccessView Output;
        internal LightBuffer(ID3D11Device device, uint count)
        {
            uint capacity = Math.Max(1u, count);
            buffer = device.CreateBuffer(new BufferDescription
            {
                ByteWidth = capacity * 24, StructureByteStride = 24, Usage = ResourceUsage.Default,
                BindFlags = BindFlags.UnorderedAccess | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.BufferStructured,
            });
            try
            {
                Output = device.CreateUnorderedAccessView(buffer, new UnorderedAccessViewDescription
                {
                    ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView { NumElements = capacity },
                });
                View = device.CreateShaderResourceView(buffer);
            }
            catch { Output?.Dispose(); buffer.Dispose(); throw; }
        }
        public void Dispose() { View.Dispose(); Output.Dispose(); buffer.Dispose(); }
    }

    internal void Prepare(Programs program, ShaderParameters? p, int width, int height,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IDictionary<string, ID3D11ShaderResourceView> sceneTextures,
        IReadOnlyDictionary<string, ID3D11SamplerState> sceneSamplers)
    {
        passBuffers.Clear(); foreach (var pair in buffers) passBuffers[pair.Key] = pair.Value;
        textures.Clear(); foreach (var pair in sceneTextures) textures[pair.Key] = pair.Value;
        textures["PointLights"] = lights!.View;
        textures["RasterAirlightLut"] = lookup!.View;
        textures["RasterLookupState"] = lookup!.State;
        samplers.Clear(); foreach (var pair in sceneSamplers) samplers[pair.Key] = pair.Value;
        samplers["RasterLutSampler"] = lookup!.Sampler;
        // Previous frame SRVs must be released before compute writes or target binding.
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        if (!ReferenceEquals(lookupPrograms, program))
        {
            lookup!.Invalidate(context);
            lookupPrograms = program;
        }
        Mark("Raster lookup", true);
        lookupOutputs.Clear(); lookupOutputs["RasterLookupOutputState"] = lookup!.StateOutput;
        program.LookupPrepare.Bind(context, p, passBuffers, textures, samplers, lookupOutputs);
        context.Dispatch(1, 1, 1);
        context.CSSetUnorderedAccessView(0, null);
        lookupOutputs.Clear(); lookupOutputs["RasterLookupOutput"] = lookup!.Output;
        program.LookupBuild.Bind(context, p, passBuffers, textures, samplers, lookupOutputs);
        context.Dispatch((uint)((lookup!.Width + 7) / 8), (uint)((lookup!.Height + 7) / 8), 1);
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
        lookupOutputs.Clear(); lookupOutputs["RasterLookupOutputState"] = lookup!.StateOutput;
        program.LookupCommit.Bind(context, p, passBuffers, textures, samplers, lookupOutputs);
        context.Dispatch(1, 1, 1);
        context.CSSetUnorderedAccessView(0, null);
        Mark("Raster lookup", false);
        Mark("Raster lights prepare", true);
        program.Lights.Bind(context, p, passBuffers, textures, samplers, outputs);
        if (LightCount != 0) context.Dispatch((LightCount + 63) / 64, 1, 1);
        context.CSSetUnorderedAccessView(0, null); context.CSSetUnorderedAccessView(1, null);
        context.CSSetShaderResources(0, ShaderProgram.EmptyViews); context.CSSetShader(null);
        Mark("Raster lights prepare", false);
        program.Geometry.Vertex.Bind(context, p, passBuffers, textures, samplers);
        program.Surface.Bind(context, p, passBuffers, textures, samplers);
        context.OMSetBlendState(null); context.OMSetDepthStencilState(depthState);
        context.RSSetViewport(0, 0, surface!.Width, surface.Height);
        context.ClearRenderTargetView(surface!.Color!, new(0, 0, 0, 0));
        context.ClearDepthStencilView(depth!.Depth!, DepthStencilClearFlags.Depth, 1, 0);
        context.OMSetRenderTargets(surface.Color!, depth.Depth);
        Mark("Raster surface", true); RasterGeometry.Draw(context, AllocatedSizes.Vertices, AllocatedSizes.Instances); Mark("Raster surface", false);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.OMSetDepthStencilState(null);
        textures["RasterDepth"] = depth.View;
        sceneTextures["RasterSurface"] = surface.View; sceneTextures["RasterDepth"] = depth.View;
        sceneTextures["RasterAirlight"] = air!.View; sceneTextures["PointLights"] = lights!.View;
        sceneTextures["RasterAirlightLut"] = lookup!.View; sceneTextures["RasterLookupState"] = lookup!.State;
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
        surface?.Dispose(); depth?.Dispose(); air?.Dispose(); lights?.Dispose(); lookup?.Dispose();
        for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); owned.Clear();
    }
}

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Independent point-only pass. CPU work is resource scheduling and medium/LUT setup.
internal sealed class ProceduralPointLightTransport : IDisposable
{
    internal const int Capacity = 512;
    internal sealed class Programs : IDisposable
    {
        readonly List<IDisposable> owned = [];
        internal readonly ShaderProgram Lights, Surface, Airlight;
        internal readonly RasterGeometry.Programs Geometry;
        internal Programs(ID3D11Device device, string source, string file)
        {
            try
            {
                Lights = Own(new ShaderProgram(device, source, "PreparePointLights", file, "cs_5_0"));
                Surface = Own(new ShaderProgram(device, source, "PointSurfacePS", file));
                Airlight = Own(new ShaderProgram(device, source, "PointAirlightPS", file));
                Geometry = Own(new RasterGeometry.Programs(device, source, file));
            }
            catch { Dispose(); throw; }
        }
        T Own<T>(T resource) where T : IDisposable { owned.Add(resource); return resource; }
        public void Dispose() { for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); owned.Clear(); }
    }

    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly UnshadowedAirlightLookup lookup;
    readonly List<IDisposable> owned = [];
    readonly RasterGeometry geometry;
    readonly ID3D11DepthStencilState depthState;
    readonly Dictionary<string, ID3D11UnorderedAccessView> outputs = [];
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = [];
    readonly Dictionary<string, ID3D11SamplerState> samplers = [];
    readonly ID3D11ShaderResourceView lightsView, countView;
    Target? surface, depth, air;
    internal Action<string, bool>? PassMarker { get; set; }

    internal ProceduralPointLightTransport(ID3D11Device device, ID3D11DeviceContext context, UnshadowedAirlightLookup lookup)
    {
        this.device = device; this.context = context; this.lookup = lookup;
        try
        {
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

    internal void Prepare(Programs program, ShaderParameters p, int width, int height,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IDictionary<string, ID3D11ShaderResourceView> sceneTextures,
        IReadOnlyDictionary<string, ID3D11SamplerState> sceneSamplers)
    {
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
        samplers.Clear(); foreach (var pair in sceneSamplers) samplers[pair.Key] = pair.Value;
        samplers["RasterLutSampler"] = lookup.Sampler;
        // Previous frame SRVs must be released before compute writes or target binding.
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        Mark("Point lights prepare", true);
        program.Lights.Bind(context, p, buffers, textures, samplers, outputs);
        context.Dispatch((Capacity + 63) / 64, 1, 1);
        context.CSSetUnorderedAccessView(0, null); context.CSSetUnorderedAccessView(1, null);
        context.CSSetShaderResources(0, ShaderProgram.EmptyViews); context.CSSetShader(null);
        Mark("Point lights prepare", false);
        Mark("Point geometry prepare", true);
        geometry.Prepare(context, program.Geometry, p, buffers, textures, samplers);
        Mark("Point geometry prepare", false);
        program.Geometry.Vertex.Bind(context, p, buffers, textures, samplers);
        program.Surface.Bind(context, p, buffers, textures, samplers);
        context.OMSetBlendState(null); context.OMSetDepthStencilState(depthState);
        context.RSSetViewport(0, 0, width, height);
        context.ClearRenderTargetView(surface.Color!, new(0, 0, 0, 0));
        context.ClearDepthStencilView(depth!.Depth!, DepthStencilClearFlags.Depth, 1, 0);
        context.OMSetRenderTargets(surface.Color!, depth.Depth);
        Mark("Point surface", true); geometry.Draw(context); Mark("Point surface", false);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.OMSetDepthStencilState(null);
        textures["PointDepth"] = depth.View;
        sceneTextures["PointSurface"] = surface.View; sceneTextures["PointDepth"] = depth.View;
        sceneTextures["PointAirlight"] = air.View; sceneTextures["PointLights"] = lightsView;
        sceneTextures["PointLightCount"] = countView; sceneTextures["RasterAirlightLut"] = lookup.View;
    }
    internal void IntegrateAirlight(Programs program, ShaderParameters p, IReadOnlyDictionary<string, ID3D11Buffer> buffers)
    {
        if (p.GetBoolean(Find(p, "View", "ShowDepth"))) return;
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(air!.Color!);
        context.RSSetViewport(0, 0, air.Width, air.Height);
        program.Airlight.Bind(context, p, buffers, textures, samplers);
        Mark("Point airlight", true); context.Draw(3, 0); Mark("Point airlight", false);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.RSSetViewport(0, 0, surface!.Width, surface.Height);
    }
    void Mark(string name, bool begin) => PassMarker?.Invoke(name, begin);
    static ShaderParameter Find(ShaderParameters p, string group, string name) =>
        p.Items.Single(x => x.Name == group).Children.Single(x => x.Name == name);

    // A camera render target or depth buffer; no shadow resources exist in this pass.
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

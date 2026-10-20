using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Opaque instanced geometry. One depth per light direction; homogeneous air is
// integrated in the subsequent fullscreen pass, independently of object count.
internal sealed class RasterLightTransport : IDisposable
{
    internal sealed class Programs : IDisposable
    {
        internal readonly ShaderProgram Surface;
        internal readonly ID3D11VertexShader Vertex;
        internal Programs(ID3D11Device device, string source, string file)
        {
            Surface = new(device, source, "RasterSurfacePS", file);
            try { Vertex = device.CreateVertexShader(Compiler.Compile(source, "RasterGeometryVS", file, "vs_5_0").Span); }
            catch { Surface.Dispose(); throw; }
        }
        public void Dispose() { Vertex.Dispose(); Surface.Dispose(); }
    }

    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11Buffer selection;
    readonly ID3D11DepthStencilState depthState;
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = [];
    readonly ID3D11DepthStencilView?[] shadowTargets = new ID3D11DepthStencilView?[24];
    readonly float[]?[] keys = new float[]?[4];
    ID3D11Texture2D? shadows, color, depth;
    ID3D11ShaderResourceView? shadowView, colorView, depthView;
    ID3D11RenderTargetView? colorTarget;
    ID3D11DepthStencilView? depthTarget;
    Programs? previousProgram;
    int size, width, height;
    internal Action<string, bool>? PassMarker { get; set; }

    internal RasterLightTransport(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device = device; this.context = context;
        selection = device.CreateBuffer(16, BindFlags.ConstantBuffer);
        try
        {
            depthState = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true, DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.LessEqual, StencilEnable = false,
            });
        }
        catch { selection.Dispose(); throw; }
    }
    static ShaderParameter Find(ShaderParameters p, string group, string name) =>
        p.Items.Single(x => x.Name == group).Children.Single(x => x.Name == name);
    static float Read(ShaderParameters p, string group, string name) => p.GetFloat(Find(p, group, name));
    static bool Flag(ShaderParameters p, string group, string name) => p.GetBoolean(Find(p, group, name));

    ID3D11Texture2D CreateDepth(int w, int h, int layers) => device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)w, Height = (uint)h, ArraySize = (uint)layers, MipLevels = 1,
        Format = Format.R32_Typeless, SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
        BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
    });
    void AllocateShadows(int resolution)
    {
        ReleaseShadows();
        try
        {
            shadows = CreateDepth(resolution, resolution, 24);
            shadowView = device.CreateShaderResourceView(shadows, new ShaderResourceViewDescription
            {
                Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayShaderResourceView { MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = 0, ArraySize = 24 },
            });
            for (int face = 0; face < 24; face++)
                shadowTargets[face] = device.CreateDepthStencilView(shadows, new DepthStencilViewDescription
                {
                    Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2DArray,
                    Texture2DArray = new Texture2DArrayDepthStencilView { MipSlice = 0, FirstArraySlice = (uint)face, ArraySize = 1 },
                });
            size = resolution;
        }
        catch { ReleaseShadows(); throw; }
    }
    void AllocateCamera(int w, int h)
    {
        ReleaseCamera();
        try
        {
            color = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, ArraySize = 1, MipLevels = 1,
                Format = Format.R16G16B16A16_Float, SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            colorTarget = device.CreateRenderTargetView(color); colorView = device.CreateShaderResourceView(color);
            depth = CreateDepth(w, h, 1);
            depthTarget = device.CreateDepthStencilView(depth, new DepthStencilViewDescription
                { Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D });
            depthView = device.CreateShaderResourceView(depth, new ShaderResourceViewDescription
            {
                Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
            });
            width = w; height = h;
        }
        catch { ReleaseCamera(); throw; }
    }
    internal void Prepare(Programs program, ShaderParameters p, float time, int w, int h,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IDictionary<string, ID3D11ShaderResourceView> sceneTextures,
        IReadOnlyDictionary<string, ID3D11SamplerState> samplers)
    {
        int resolution = 128 << (int)MathF.Round(2 * Read(p, "Quality", "ShadowResolution"));
        if (resolution != size) AllocateShadows(resolution);
        if (width != w || height != h) AllocateCamera(w, h);
        if (!ReferenceEquals(previousProgram, program)) { Array.Clear(keys); previousProgram = program; }
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.OMSetBlendState(null);
        textures.Clear(); textures["RasterShadows"] = shadowView!;
        // This bind supplies the shared b0/b1 layouts to both stages. The shadow
        // draws use only the vertex shader; unbind all SRVs before attaching DSVs.
        program.Surface.Bind(context, p, buffers, textures, samplers, vertexConstants: true);
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.PSSetShader(null);
        context.VSSetShader(program.Vertex);
        context.VSSetConstantBuffer(2, selection);
        context.OMSetDepthStencilState(depthState);
        int count = Flag(p, "Shape", "Enabled") ? 1 + (int)MathF.Round(4095 * Read(p, "Shape", "Count")) : 0;
        bool preview = Flag(p, "View", "ShowShadows");
        int previewLight = (int)MathF.Round(3 * Read(p, "View", "ShadowLight"));
        for (int light = 0; light < 4; light++)
        {
            string group = light == 0 ? "Light" : "Light" + (light + 1);
            if (!(preview && light == previewLight)
                && (!Flag(p, group, "Enabled") || Read(p, group, "Intensity") <= 0)) continue;
            float motion = Read(p, "Shape", "Speed"), speed = Read(p, group, "Speed");
            float[] key = [count, Read(p, "Shape", "Seed"), Read(p, "Shape", "Size"), Read(p, "Shape", "Spread"),
                motion, speed, Read(p, group, "Orbit"), Read(p, group, "Phase"), motion > 0 || speed > 0 ? time : 0];
            if (keys[light] != null && key.AsSpan().SequenceEqual(keys[light])) continue;
            context.RSSetViewport(0, 0, size, size);
            PassMarker?.Invoke($"Raster light {light} cube", true);
            for (int face = 0; face < 6; face++)
            {
                context.UpdateSubresource(new Selection((uint)light, (uint)face, 1), selection);
                var target = shadowTargets[6 * light + face]!;
                context.ClearDepthStencilView(target, DepthStencilClearFlags.Depth, 1, 0);
                context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), target);
                if (count > 0) context.DrawInstanced(36, (uint)count, 0, 0);
            }
            PassMarker?.Invoke($"Raster light {light} cube", false);
            context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            keys[light] = key;
        }
        context.RSSetViewport(0, 0, w, h);
        program.Surface.Bind(context, p, buffers, textures, samplers, vertexConstants: true);
        context.UpdateSubresource(new Selection(0, 0, 0), selection);
        context.ClearRenderTargetView(colorTarget!, new(0, 0, 0, 0));
        context.ClearDepthStencilView(depthTarget!, DepthStencilClearFlags.Depth, 1, 0);
        context.OMSetRenderTargets(colorTarget!, depthTarget!);
        PassMarker?.Invoke("Raster surface", true);
        if (count > 0) context.DrawInstanced(36, (uint)count, 0, 0);
        PassMarker?.Invoke("Raster surface", false);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.OMSetDepthStencilState(null);
        sceneTextures["RasterShadows"] = shadowView!;
        sceneTextures["RasterSurface"] = colorView!;
        sceneTextures["RasterDepth"] = depthView!;
    }
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Selection(uint Light, uint Face, uint Shadow, uint Padding = 0);
    void ReleaseShadows()
    {
        foreach (var target in shadowTargets) target?.Dispose(); Array.Clear(shadowTargets);
        shadowView?.Dispose(); shadowView = null; shadows?.Dispose(); shadows = null;
        Array.Clear(keys); size = 0;
    }
    void ReleaseCamera()
    {
        colorTarget?.Dispose(); colorTarget = null; colorView?.Dispose(); colorView = null; color?.Dispose(); color = null;
        depthTarget?.Dispose(); depthTarget = null; depthView?.Dispose(); depthView = null; depth?.Dispose(); depth = null;
        width = height = 0;
    }
    public void Dispose() { ReleaseCamera(); ReleaseShadows(); selection.Dispose(); depthState.Dispose(); }
}

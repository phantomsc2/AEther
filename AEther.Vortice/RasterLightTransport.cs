using System.Runtime.InteropServices;
using System.Numerics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Opaque instanced geometry. One depth per light direction; homogeneous air is
// integrated in the subsequent fullscreen pass, independently of object count.
internal sealed class RasterLightTransport : IDisposable
{
    internal const int MaximumLights = 32;
    internal sealed class Programs : IDisposable
    {
        internal readonly ShaderProgram Surface;
        internal readonly ShaderProgram Airlight;
        internal readonly ID3D11VertexShader Vertex;
        internal Programs(ID3D11Device device, string source, string file)
        {
            Surface = new(device, source, "RasterSurfacePS", file);
            ShaderProgram? nextAirlight = null;
            try
            {
                nextAirlight = new(device, source, "RasterAirlightPS", file);
                Vertex = device.CreateVertexShader(Compiler.Compile(source, "RasterGeometryVS", file, "vs_5_0").Span);
                Airlight = nextAirlight;
            }
            catch { nextAirlight?.Dispose(); Surface.Dispose(); throw; }
        }
        public void Dispose() { Vertex.Dispose(); Airlight.Dispose(); Surface.Dispose(); }
    }

    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11Buffer selection;
    readonly ID3D11Buffer lights;
    // Header, position/radius^2, RGB radiance, then packed active-light indices.
    readonly Vector4[] lightData = new Vector4[1 + 2 * MaximumLights + MaximumLights / 4];
    readonly ID3D11DepthStencilState depthState;
    readonly ID3D11SamplerState shadowSampler;
    readonly Dictionary<string, ID3D11SamplerState> rasterSamplers = [];
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = [];
    readonly ID3D11DepthStencilView?[] shadowTargets = new ID3D11DepthStencilView?[6 * MaximumLights];
    readonly float[]?[] keys = new float[]?[MaximumLights];
    ID3D11Texture2D? shadows, color, depth, airlight;
    ID3D11ShaderResourceView? shadowView, colorView, depthView, airlightView;
    ID3D11RenderTargetView? colorTarget, airlightTarget;
    ID3D11DepthStencilView? depthTarget;
    Programs? previousProgram;
    int size, lightCount, width, height, airWidth, airHeight;
    internal Action<string, bool>? PassMarker { get; set; }
    internal ID3D11SamplerState ShadowSampler => shadowSampler;
    internal ID3D11Buffer LightBuffer => lights;

    internal RasterLightTransport(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.device = device; this.context = context;
        selection = device.CreateBuffer(16, BindFlags.ConstantBuffer);
        try { lights = device.CreateBuffer((uint)(16 * lightData.Length), BindFlags.ConstantBuffer); }
        catch { selection.Dispose(); throw; }
        try
        {
            depthState = device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = true, DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunction.LessEqual, StencilEnable = false,
            });
            try
            {
                shadowSampler = device.CreateSamplerState(new SamplerDescription
                {
                    Filter = Filter.ComparisonMinMagLinearMipPoint,
                    AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                    ComparisonFunc = ComparisonFunction.LessEqual, MinLOD = 0, MaxLOD = 0,
                });
            }
            catch { depthState.Dispose(); throw; }
        }
        catch { lights.Dispose(); selection.Dispose(); throw; }
    }
    static ShaderParameter Find(ShaderParameters p, string group, string name) =>
        p.Items.Single(x => x.Name == group).Children.Single(x => x.Name == name);
    static float Read(ShaderParameters p, string group, string name) => p.GetFloat(Find(p, group, name));
    static bool Flag(ShaderParameters p, string group, string name) => p.GetBoolean(Find(p, group, name));

    unsafe void UpdateLights(ShaderParameters p, AudioHistory audio, int count)
    {
        Array.Clear(lightData);
        float radius = .01f + .49f * MathF.Pow(Read(p, "Lights", "Radius"), 2);
        float ringRadius = (2 + 10 * Read(p, "Shape", "Spread")) * Read(p, "Lights", "RingRadius");
        float gain = Flag(p, "Lights", "Enabled") ? 3000 * MathF.Pow(2, 12 * Read(p, "Lights", "Gain") - 6) : 0;
        // Fixed world-space plane facing the initial camera. Camera motion does
        // not rotate the ring or change its frequency/stereo coordinates.
        Vector3 forward = Vector3.Normalize(new(-9, -6, 13.5f));
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, forward);
        int active = 0;
        for (int i = 0; i < count; i++)
        {
            float angle = 2 * MathF.PI * i / count;
            float x = MathF.Sin(angle), y = MathF.Cos(angle);
            // RingEmitter UV has downward-positive Y; this world-space ring
            // uses upward-positive Y. Bass belongs below, treble above.
            float frequency = MathF.Atan2(MathF.Abs(x), -y) / MathF.PI;
            float stereo = .5f + .5f * x * MathF.Abs(x);
            float left = TransientAt(audio.Spectrum[0], audio.Bands, frequency);
            float rightT = TransientAt(audio.Spectrum[1], audio.Bands, frequency);
            float transient = MathF.Min(1e20f, left + (rightT - left) * stereo);
            Vector3 color = new(Palette(.04f), Palette(.38f), Palette(.62f));
            float Palette(float phase) => .5f + .5f * MathF.Cos(2 * MathF.PI * (frequency * .85f + phase));
            lightData[1 + i] = new(ringRadius * (x * right + y * up), radius * radius);
            lightData[1 + MaximumLights + i] = new(color * (gain * transient), gain * transient);
            if (gain * transient <= 0) continue;
            int slot = 1 + 2 * MaximumLights + active / 4;
            Vector4 indices = lightData[slot]; indices[active % 4] = i; lightData[slot] = indices;
            active++;
        }
        lightData[0] = new(count, active, 0, 0);
        fixed (Vector4* source = lightData) context.UpdateSubresource(lights, 0, null, (IntPtr)source, 0, 0);
    }
    static float TransientAt(float[] spectrum, int bands, float frequency)
    {
        // Exactly the clamped linear texture addressing used by the other
        // shaders: UV.x=frequency, UV.y=.5, green = instantaneous positive T.
        float at = Math.Clamp(frequency, 0, 1) * bands - .5f;
        int first = (int)MathF.Floor(at); float fraction = at - first;
        float a = spectrum[4 * Math.Clamp(first, 0, bands - 1) + 1];
        float b = spectrum[4 * Math.Clamp(first + 1, 0, bands - 1) + 1];
        return MathF.Max(0, a + (b - a) * fraction);
    }

    ID3D11Texture2D CreateDepth(int w, int h, int layers) => device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)w, Height = (uint)h, ArraySize = (uint)layers, MipLevels = 1,
        Format = Format.R32_Typeless, SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
        BindFlags = BindFlags.DepthStencil | BindFlags.ShaderResource,
    });
    void AllocateShadows(int resolution, int count)
    {
        ReleaseShadows();
        try
        {
            shadows = CreateDepth(resolution, resolution, 6 * count);
            shadowView = device.CreateShaderResourceView(shadows, new ShaderResourceViewDescription
            {
                Format = Format.R32_Float, ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                Texture2DArray = new Texture2DArrayShaderResourceView { MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = 0, ArraySize = (uint)(6 * count) },
            });
            for (int face = 0; face < 6 * count; face++)
                shadowTargets[face] = device.CreateDepthStencilView(shadows, new DepthStencilViewDescription
                {
                    Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2DArray,
                    Texture2DArray = new Texture2DArrayDepthStencilView { MipSlice = 0, FirstArraySlice = (uint)face, ArraySize = 1 },
                });
            size = resolution; lightCount = count;
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
    void AllocateAirlight(int w, int h)
    {
        ReleaseAirlight();
        try
        {
            airlight = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, ArraySize = 1, MipLevels = 1,
                Format = Format.R16G16B16A16_Float, SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            airlightTarget = device.CreateRenderTargetView(airlight); airlightView = device.CreateShaderResourceView(airlight);
            airWidth = w; airHeight = h;
        }
        catch { ReleaseAirlight(); throw; }
    }
    internal void Prepare(Programs program, ShaderParameters p, AudioHistory audio, float time, int w, int h,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        IDictionary<string, ID3D11ShaderResourceView> sceneTextures,
        IReadOnlyDictionary<string, ID3D11SamplerState> samplers)
    {
        int resolution = 128 << (int)MathF.Round(2 * Read(p, "Quality", "ShadowResolution"));
        int ringCount = 1 + (int)MathF.Round((MaximumLights - 1) * Read(p, "Lights", "Count"));
        if (resolution + 2 != size || lightCount != ringCount) AllocateShadows(resolution + 2, ringCount);
        UpdateLights(p, audio, ringCount);
        if (width != w || height != h) AllocateCamera(w, h);
        bool half = Flag(p, "Quality", "HalfResolutionAirlight");
        int nextAirWidth = half ? (w + 1) / 2 : w, nextAirHeight = half ? (h + 1) / 2 : h;
        if (airWidth != nextAirWidth || airHeight != nextAirHeight) AllocateAirlight(nextAirWidth, nextAirHeight);
        if (!ReferenceEquals(previousProgram, program)) { Array.Clear(keys); previousProgram = program; }
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.OMSetBlendState(null);
        textures.Clear(); textures["RasterShadows"] = shadowView!;
        rasterSamplers.Clear(); foreach (var pair in samplers) rasterSamplers[pair.Key] = pair.Value;
        rasterSamplers["RasterShadowSampler"] = shadowSampler;
        // Surface PS can omit FrameConstants now that lights are static. The
        // geometry VS still needs it for camera projection and cube motion.
        program.Surface.Bind(context, p, buffers, textures, rasterSamplers, vertexConstants: true);
        context.VSSetConstantBuffer(0, buffers["FrameConstants"]);
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.PSSetShader(null);
        context.VSSetShader(program.Vertex);
        context.VSSetConstantBuffer(2, selection);
        context.VSSetConstantBuffer(3, lights);
        context.OMSetDepthStencilState(depthState);
        int count = Flag(p, "Shape", "Enabled") ? 1 + (int)MathF.Round(4095 * Read(p, "Shape", "Count")) : 0;
        bool preview = Flag(p, "View", "ShowShadows");
        int previewLight = (int)MathF.Round((ringCount - 1) * Read(p, "View", "ShadowLight"));
        for (int light = 0; light < ringCount; light++)
        {
            if (!(preview && light == previewLight)
                && lightData[1 + MaximumLights + light].W <= 0) continue;
            float motion = Read(p, "Shape", "Speed");
            Vector4 position = lightData[1 + light];
            float[] key = [count, Read(p, "Shape", "Seed"), Read(p, "Shape", "Size"), Read(p, "Shape", "Spread"),
                motion, position.X, position.Y, position.Z, motion > 0 ? time : 0];
            if (keys[light] != null && key.AsSpan().SequenceEqual(keys[light])) continue;
            context.RSSetViewport(0, 0, size, size);
            PassMarker?.Invoke($"Raster light {light} cube", true);
            for (int face = 0; face < 6; face++)
            {
                context.UpdateSubresource(new Selection((uint)light, (uint)face, 1, (size - 2f) / size), selection);
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
        program.Surface.Bind(context, p, buffers, textures, rasterSamplers, vertexConstants: true);
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
        sceneTextures["RasterAirlight"] = airlightView!;
        textures["RasterDepth"] = depthView!;
    }
    internal void IntegrateAirlight(Programs program, ShaderParameters p,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers)
    {
        if (Flag(p, "View", "ShowDepth") || Flag(p, "View", "ShowShadows")) return;
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(airlightTarget!);
        context.RSSetViewport(0, 0, airWidth, airHeight);
        program.Airlight.Bind(context, p, buffers, textures, rasterSamplers);
        PassMarker?.Invoke("Raster airlight", true);
        context.Draw(3, 0);
        PassMarker?.Invoke("Raster airlight", false);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.RSSetViewport(0, 0, width, height);
    }
    [StructLayout(LayoutKind.Sequential)]
    readonly record struct Selection(uint Light, uint Face, uint Shadow, float ShadowScale = 1);
    void ReleaseShadows()
    {
        foreach (var target in shadowTargets) target?.Dispose(); Array.Clear(shadowTargets);
        shadowView?.Dispose(); shadowView = null; shadows?.Dispose(); shadows = null;
        Array.Clear(keys); size = lightCount = 0;
    }
    void ReleaseCamera()
    {
        colorTarget?.Dispose(); colorTarget = null; colorView?.Dispose(); colorView = null; color?.Dispose(); color = null;
        depthTarget?.Dispose(); depthTarget = null; depthView?.Dispose(); depthView = null; depth?.Dispose(); depth = null;
        ReleaseAirlight();
        width = height = 0;
    }
    void ReleaseAirlight()
    {
        airlightTarget?.Dispose(); airlightTarget = null; airlightView?.Dispose(); airlightView = null; airlight?.Dispose(); airlight = null;
        airWidth = airHeight = 0;
    }
    public void Dispose() { ReleaseCamera(); ReleaseShadows(); lights.Dispose(); selection.Dispose(); depthState.Dispose(); shadowSampler.Dispose(); }
}

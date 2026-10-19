using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Shared atmosphere/G-buffer, independent light transport and integration draws.
internal sealed class EpipolarShadowMap : IDisposable
{
    readonly ID3D11DeviceContext context;
    readonly ID3D11Device device;
    readonly List<IDisposable> resources = [];
    readonly MandelFluidSimulation.Surface atmosphere;
    readonly MandelFluidSimulation.Surface?[] shadows = new MandelFluidSimulation.Surface?[4];
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = [];
    readonly Dictionary<string, ID3D11SamplerState> samplers = [];
    readonly Dictionary<string, ID3D11Buffer> lightBuffers = [];
    readonly ID3D11SamplerState sampler;
    readonly ID3D11Buffer lightConstants;
    readonly ID3D11BlendState additive;
    ShaderProgram? previousAtmosphere, previousSurface;
    readonly ShaderProgram?[] previousLights = new ShaderProgram?[4];
    readonly float[]?[] previousLightKeys = new float[]?[4];
    float[]? previousSurfaceKey;
    float previousHeight;
    int previewLight;
    MandelFluidSimulation.Surface? surfaceCache, radiance;
    internal ID3D11ShaderResourceView View => Shadow(previewLight).View;
    internal ID3D11ShaderResourceView Atmosphere => atmosphere.View;
    internal ID3D11SamplerState Sampler => sampler;
    internal ID3D11ShaderResourceView SurfaceCache => surfaceCache!.View;
    internal ID3D11ShaderResourceView Radiance => radiance!.View;

    T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
    internal EpipolarShadowMap(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.context = context; this.device = device;
        try
        {
            atmosphere = Own(new MandelFluidSimulation.Surface(device, 512, 256, Format.R32_Float));
            sampler = Own(device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagLinearMipPoint,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp, MaxLOD = float.MaxValue,
                ComparisonFunc = ComparisonFunction.Never,
            }));
            samplers["AtmosphereSampler"] = sampler;
            lightConstants = Own(device.CreateBuffer(16, BindFlags.ConstantBuffer));
            var blend = BlendDescription.Opaque;
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true, SourceBlend = Blend.One, DestinationBlend = Blend.One,
                BlendOperation = BlendOperation.Add, SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.One, BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            additive = Own(device.CreateBlendState(blend));
        }
        catch { Dispose(); throw; }
    }

    static ShaderParameter Find(ShaderParameters values, string group, string name) =>
        values.Items.Single(p => p.Name == group).Children.Single(p => p.Name == name);
    static float Read(ShaderParameters values, string group, string name) => values.GetFloat(Find(values, group, name));
    static bool Flag(ShaderParameters values, string group, string name) => values.GetBoolean(Find(values, group, name));
    MandelFluidSimulation.Surface Shadow(int index) => shadows[index] ??=
        new(device, 512, 512, Format.R32G32B32A32_Float);

    internal void PrecalculateAtmosphere(ShaderProgram program, ShaderParameters parameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers)
    {
        float height = Read(parameters, "Air", "ScaleHeight");
        if (!ReferenceEquals(previousAtmosphere, program) || height != previousHeight)
        {
            textures.Clear();
            Draw(program, atmosphere, parameters, buffers);
            previousAtmosphere = program; previousHeight = height;
        }
        textures["AtmosphereOpticalDepth"] = atmosphere.View;
    }

    internal void PrecalculateSurface(ShaderProgram program, ShaderParameters parameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers, int width, int height, bool render, float time)
    {
        if (surfaceCache == null || surfaceCache.Width != width || surfaceCache.Height != height)
        {
            surfaceCache?.Dispose(); surfaceCache = null;
            surfaceCache = new(device, width, height, Format.R32G32B32A32_Float);
            previousSurface = null;
        }
        if (!render) return;
        bool split = Flag(parameters, "View", "ShowShadowMap");
        float cameraSpeed = Read(parameters, "View", "CameraSpeed");
        Span<float> key = stackalloc float[] { Read(parameters, "Fractal", "Scale"), Read(parameters, "Fractal", "Potential"),
            Read(parameters, "Fractal", "MaximumIterations"), Read(parameters, "Quality", "SearchSteps"), split ? 1 : 0,
            width, height, cameraSpeed, cameraSpeed > 0 ? time : 0 };
        if (ReferenceEquals(previousSurface, program) && key.SequenceEqual(previousSurfaceKey)) return;
        Draw(program, surfaceCache, parameters, buffers);
        previousSurface = program;
        previousSurfaceKey ??= new float[key.Length]; key.CopyTo(previousSurfaceKey);
    }

    void TransportLight(ShaderProgram program, ShaderParameters parameters, float time, int index, string group)
    {
        float speed = Read(parameters, group, "Speed"), cameraSpeed = Read(parameters, "View", "CameraSpeed");
        Span<float> key = stackalloc float[] { Read(parameters, "Fractal", "Scale"), Read(parameters, "Fractal", "Potential"),
            Read(parameters, "Fractal", "MaximumIterations"), Read(parameters, "Quality", "SearchSteps"),
            Read(parameters, "Air", "ScaleHeight"), speed, Read(parameters, group, "Phase"), cameraSpeed,
            speed > 0 || cameraSpeed > 0 ? time : 0 };
        if (ReferenceEquals(previousLights[index], program) && key.SequenceEqual(previousLightKeys[index])) return;
        Draw(program, Shadow(index), parameters, lightBuffers);
        previousLights[index] = program;
        previousLightKeys[index] ??= new float[key.Length]; key.CopyTo(previousLightKeys[index]);
    }

    internal void IntegrateLights(ShaderProgram shadowProgram, ShaderProgram lightProgram, ShaderParameters parameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers, int width, int height, float time, bool atmosphereOnly)
    {
        if (radiance == null || radiance.Width != width || radiance.Height != height)
        {
            radiance?.Dispose(); radiance = null;
            // FP32 keeps sharp point-light/specular peaks finite before tone mapping.
            radiance = new(device, width, height, Format.R32G32B32A32_Float);
        }
        previewLight = (int)MathF.Round(3 * Read(parameters, "View", "ShadowLight"));
        if (atmosphereOnly) return;
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.ClearRenderTargetView(radiance.Target, new(0, 0, 0, 0));
        lightBuffers.Clear(); foreach (var entry in buffers) lightBuffers.Add(entry.Key, entry.Value);
        lightBuffers["LightConstants"] = lightConstants;
        textures["SurfaceCache"] = surfaceCache!.View;
        bool preview = Flag(parameters, "View", "ShowShadowMap");
        try
        {
            for (int index = 0; index < 4; index++)
            {
                string group = index == 0 ? "Light" : "Light" + (index + 1);
                bool enabled = Flag(parameters, group, "Enabled") && Read(parameters, group, "Intensity") > 0
                    && Find(parameters, group, "Color").Children.Any(p => parameters.GetFloat(p) > 0);
                if (!enabled && !(preview && index == previewLight)) continue;
                context.UpdateSubresource(new LightSelection((uint)index), lightConstants);
                TransportLight(shadowProgram, parameters, time, index, group);
                if (!enabled) continue;
                textures["EpipolarShadow"] = Shadow(index).View;
                Draw(lightProgram, radiance, parameters, lightBuffers, add: true);
            }
        }
        finally { context.OMSetBlendState(null); }
    }

    void Draw(ShaderProgram shader, MandelFluidSimulation.Surface target, ShaderParameters parameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers, bool add = false)
    {
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(target.Target);
        context.OMSetBlendState(add ? additive : null);
        context.RSSetViewport(0, 0, target.Width, target.Height);
        shader.Bind(context, parameters, buffers, textures, samplers);
        context.Draw(3, 0);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct LightSelection(uint Index, uint Padding0 = 0, uint Padding1 = 0, uint Padding2 = 0);

    public void Dispose()
    {
        surfaceCache?.Dispose(); radiance?.Dispose();
        foreach (var shadow in shadows) shadow?.Dispose();
        for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
    }
}

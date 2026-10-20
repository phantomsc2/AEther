using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Moment measures for camera and angular light rays, plus shared radial air depth.
internal sealed class MomentLightTransport : IDisposable
{
    internal Action<string, bool>? PassMarker { get; set; }
    sealed class RayCache : IDisposable
    {
        internal readonly MandelFluidSimulation.Surface Material = null!, Air = null!, Totals = null!;
        internal readonly MandelFluidSimulation.Surface? Importance;
        internal readonly ID3D11RenderTargetView[] Targets;
        internal RayCache(ID3D11Device device, int width, int height, bool camera = false)
        {
            try
            {
                Material = new(device, width, height, Format.R32G32B32A32_Float);
                Air = new(device, width, height, Format.R32G32B32A32_Float);
                Totals = new(device, width, height, Format.R32G32B32A32_Float);
                if (camera) Importance = new(device, width, height, Format.R32G32B32A32_UInt);
                Targets = Importance != null ? [Material.Target, Air.Target, Totals.Target, Importance.Target]
                    : [Material.Target, Air.Target, Totals.Target];
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Material?.Dispose(); Air?.Dispose(); Totals?.Dispose(); Importance?.Dispose(); }
    }
    readonly ID3D11DeviceContext context;
    readonly ID3D11Device device;
    readonly List<IDisposable> resources = [];
    readonly MandelFluidSimulation.Surface atmosphere;
    readonly RayCache?[] lights = new RayCache?[4];
    readonly Dictionary<string, ID3D11ShaderResourceView> textures = [];
    readonly Dictionary<string, ID3D11SamplerState> samplers = [];
    readonly Dictionary<string, ID3D11Buffer> lightBuffers = [];
    readonly ID3D11SamplerState sampler;
    readonly ID3D11Buffer lightConstants;
    readonly ID3D11BlendState additive;
    ShaderProgram? previousAtmosphere, previousCamera;
    readonly ShaderProgram?[] previousLights = new ShaderProgram?[4];
    readonly float[]?[] previousLightKeys = new float[]?[4];
    float[]? previousCameraKey;
    float previousHeight;
    int previewLight;
    RayCache? camera;
    MandelFluidSimulation.Surface? radiance;
    internal ID3D11ShaderResourceView Atmosphere => atmosphere.View;
    internal ID3D11SamplerState Sampler => sampler;
    internal ID3D11ShaderResourceView Radiance => radiance!.View;

    T Own<T>(T resource) where T : IDisposable { resources.Add(resource); return resource; }
    internal MomentLightTransport(ID3D11Device device, ID3D11DeviceContext context)
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
    RayCache Light(int index) => lights[index] ??= new(device, 512, 512);

    static void BindTransport(IDictionary<string, ID3D11ShaderResourceView> target, string prefix, RayCache cache)
    {
        target[prefix + "Moments"] = cache.Material.View;
        target[prefix + "AirMoments"] = cache.Air.View;
        target[prefix + "Transport"] = cache.Totals.View;
        if (cache.Importance != null) target[prefix + "Importance"] = cache.Importance.View;
    }
    internal void BindPreview(IDictionary<string, ID3D11ShaderResourceView> target) =>
        BindTransport(target, "Light", Light(previewLight));
    internal void BindCamera(IDictionary<string, ID3D11ShaderResourceView> target) =>
        BindTransport(target, "Camera", camera!);

    internal void PrecalculateAtmosphere(ShaderProgram program, ShaderParameters parameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers)
    {
        float height = Read(parameters, "Air", "ScaleHeight");
        if (!ReferenceEquals(previousAtmosphere, program) || height != previousHeight)
        {
            textures.Clear();
            Draw(program, atmosphere, parameters, buffers, label: "Atmosphere");
            previousAtmosphere = program; previousHeight = height;
        }
        textures["AtmosphereOpticalDepth"] = atmosphere.View;
    }

    internal void PrecalculateCamera(ShaderProgram program, ShaderParameters parameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers, int width, int height, bool render, float time)
    {
        if (camera == null || camera.Material.Width != width || camera.Material.Height != height)
        {
            camera?.Dispose(); camera = null;
            camera = new(device, width, height, camera: true);
            previousCamera = null;
        }
        if (!render) return;
        bool split = Flag(parameters, "View", "ShowLightTransport");
        float cameraSpeed = Read(parameters, "View", "CameraSpeed");
        Span<float> key = stackalloc float[] { Read(parameters, "Fractal", "Scale"), Read(parameters, "Fractal", "Potential"),
            Read(parameters, "Fractal", "MaximumIterations"), Read(parameters, "Quality", "TransportSteps"),
            Flag(parameters, "Interior", "Enabled") ? 1 : 0, Read(parameters, "Interior", "EdgeSoftness"),
            Flag(parameters, "Transport", "Trigonometric") ? 1 : 0, Read(parameters, "Air", "ScaleHeight"), split ? 1 : 0,
            parameters.Items.Single(p => p.Name == "Transport").Children.Any(p => p.Name == "OrbitRejection") && Flag(parameters, "Transport", "OrbitRejection") ? 1 : 0,
            width, height, cameraSpeed, Read(parameters, "View", "CameraDistance"), Read(parameters, "View", "HorizontalOffset"),
            cameraSpeed > 0 ? time : 0 };
        if (!ReferenceEquals(previousCamera, program) || !key.SequenceEqual(previousCameraKey))
        {
            Draw(program, camera.Targets, width, height, parameters, buffers, label: "Camera transport");
            previousCamera = program;
            previousCameraKey ??= new float[key.Length]; key.CopyTo(previousCameraKey);
        }
        BindTransport(textures, "Camera", camera);
    }

    void TransportLight(ShaderProgram program, ShaderParameters parameters, float time, int index, string group)
    {
        float speed = Read(parameters, group, "Speed"), cameraSpeed = Read(parameters, "View", "CameraSpeed");
        Span<float> key = stackalloc float[] { Read(parameters, "Fractal", "Scale"), Read(parameters, "Fractal", "Potential"),
            Read(parameters, "Fractal", "MaximumIterations"), Read(parameters, "Quality", "TransportSteps"),
            Flag(parameters, "Interior", "Enabled") ? 1 : 0, Read(parameters, "Interior", "EdgeSoftness"),
            Flag(parameters, "Transport", "Trigonometric") ? 1 : 0,
            parameters.Items.Single(p => p.Name == "Transport").Children.Any(p => p.Name == "OrbitRejection") && Flag(parameters, "Transport", "OrbitRejection") ? 1 : 0,
            Read(parameters, "Air", "ScaleHeight"), speed, Read(parameters, group, "Phase"), cameraSpeed,
            Read(parameters, "View", "CameraDistance"), Read(parameters, "View", "HorizontalOffset"),
            speed > 0 || cameraSpeed > 0 ? time : 0 };
        if (ReferenceEquals(previousLights[index], program) && key.SequenceEqual(previousLightKeys[index])) return;
        Draw(program, Light(index).Targets, 512, 512, parameters, lightBuffers, label: $"Light {index} transport");
        previousLights[index] = program;
        previousLightKeys[index] ??= new float[key.Length]; key.CopyTo(previousLightKeys[index]);
    }

    internal void IntegrateLights(ShaderProgram transportProgram, ShaderProgram lightProgram, ShaderParameters parameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers, int width, int height, float time, bool atmosphereOnly)
    {
        if (radiance == null || radiance.Width != width || radiance.Height != height)
        {
            radiance?.Dispose(); radiance = null;
            // FP32 preserves bright point-light and emission peaks before tone mapping.
            radiance = new(device, width, height, Format.R32G32B32A32_Float);
        }
        previewLight = (int)MathF.Round(3 * Read(parameters, "View", "TransportLight"));
        if (atmosphereOnly) return;
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.ClearRenderTargetView(radiance.Target, new(0, 0, 0, 0));
        lightBuffers.Clear(); foreach (var entry in buffers) lightBuffers.Add(entry.Key, entry.Value);
        lightBuffers["LightConstants"] = lightConstants;
        bool preview = Flag(parameters, "View", "ShowLightTransport");
        try
        {
            // Emission is independent of enabled lights and must be added once.
            // The shared entry point requires valid light bindings even when
            // its emission-only branch reads camera transport exclusively.
            if (Flag(parameters, "Interior", "Enabled") && Flag(parameters, "Emission", "Enabled")
                && Read(parameters, "Emission", "Strength") > 0
                && Find(parameters, "Emission", "Color").Children.Any(p => parameters.GetFloat(p) > 0))
            {
                context.UpdateSubresource(new LightSelection(4), lightConstants);
                BindTransport(textures, "Light", camera!);
                Draw(lightProgram, radiance, parameters, lightBuffers, add: true, label: "Emission");
            }
            for (int index = 0; index < 4; index++)
            {
                string group = index == 0 ? "Light" : "Light" + (index + 1);
                bool enabled = Flag(parameters, group, "Enabled") && Read(parameters, group, "Intensity") > 0
                    && Find(parameters, group, "Color").Children.Any(p => parameters.GetFloat(p) > 0);
                if (!enabled && !(preview && index == previewLight)) continue;
                context.UpdateSubresource(new LightSelection((uint)index), lightConstants);
                TransportLight(transportProgram, parameters, time, index, group);
                if (!enabled) continue;
                BindTransport(textures, "Light", Light(index));
                Draw(lightProgram, radiance, parameters, lightBuffers, add: true, label: $"Light {index} integration");
            }
        }
        finally { context.OMSetBlendState(null); }
    }

    void Draw(ShaderProgram shader, MandelFluidSimulation.Surface target, ShaderParameters parameters,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers, bool add = false, string label = "")
        => Draw(shader, [target.Target], target.Width, target.Height, parameters, buffers, add, label);

    void Draw(ShaderProgram shader, ID3D11RenderTargetView[] targets, int width, int height,
        ShaderParameters parameters, IReadOnlyDictionary<string, ID3D11Buffer> buffers, bool add = false, string label = "")
    {
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(targets);
        context.OMSetBlendState(add ? additive : null);
        context.RSSetViewport(0, 0, width, height);
        shader.Bind(context, parameters, buffers, textures, samplers);
        PassMarker?.Invoke(label, true);
        context.Draw(3, 0);
        PassMarker?.Invoke(label, false);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct LightSelection(uint Index, uint Padding0 = 0, uint Padding1 = 0, uint Padding2 = 0);

    public void Dispose()
    {
        camera?.Dispose(); radiance?.Dispose();
        foreach (var light in lights) light?.Dispose();
        for (int i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
    }
}

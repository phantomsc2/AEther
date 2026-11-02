using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

/// <summary>Shared atmosphere LUT lifecycle for ground and orbital views.</summary>
public sealed class AtmosphereEffect : SceneEffect
{
    internal const int TransmittanceWidth = 256, TransmittanceHeight = 64;
    internal const int SkyViewWidth = 256, SkyViewHeight = 128;
    readonly ShaderProgram transmittanceProgram, skyViewProgram, multipleProgram;
    readonly int skyViewWidth, skyViewHeight;
    readonly ID3D11SamplerState sampler;
    readonly Dictionary<string, ID3D11UnorderedAccessView> outputs = [];
    Lookup? transmittance, skySun, skySun2, skyMultiple, multipleScattering;
    bool multipleDirty = true;
    readonly ShaderProgram? aerialProgram;
    readonly FroxelRaster? meteors;
    Volume? aerialSun, aerialSun2, aerialMultiple, aerialTransmission;
    float[]? aerialKey;
    float aerialAspect;
    // Internal override permits resolution comparisons without changing the public scene controls.
    internal int AerialSize { get; set; } = 32;
    ShaderParameters? previous;
    float[]? atmosphereKey, sunKey;

    // Internal texture access supports numerical GPU checks without display quantization.
    internal ID3D11Texture3D AerialSunTexture => aerialSun!.Texture;
    internal ID3D11Texture3D AerialSun2Texture => aerialSun2!.Texture;
    internal ID3D11Texture3D AerialMultipleTexture => aerialMultiple!.Texture;
    internal ID3D11Texture3D AerialTransmissionTexture => aerialTransmission!.Texture;
    internal ID3D11Texture2D TransmittanceTexture => transmittance!.Texture;
    internal ID3D11Texture2D SkySunTexture => skySun!.Texture;
    internal ID3D11Texture2D SkySun2Texture => skySun2!.Texture;
    internal ID3D11Texture2D SkyMultipleTexture => skyMultiple!.Texture;
    internal ID3D11Texture2D MultipleScatteringTexture => multipleScattering!.Texture;

    internal AtmosphereEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file,
        int skyViewWidth = SkyViewWidth, int skyViewHeight = SkyViewHeight)
        : base(name, device, context, source, file)
    {
        this.skyViewWidth = skyViewWidth; this.skyViewHeight = skyViewHeight;
        try
        {
            transmittanceProgram = new(device, source, "BuildTransmittance", file, "cs_5_0");
            skyViewProgram = new(device, source, "BuildSkyView", file, "cs_5_0");
            multipleProgram = new(device, source, "BuildMultipleScattering", file, "cs_5_0");
            if (file == "Planet.hlsl")
            {
                aerialProgram = new(device, source, "BuildAerial", file, "cs_5_0");
                meteors = new(device, context, source, file, marker: "Planet meteors");
            }
            var programs = new[] { Display, transmittanceProgram, skyViewProgram, multipleProgram }
                .Concat(aerialProgram == null ? [] : new[] { aerialProgram }).ToArray();
            var bindings = FrameBindings.Concat(["AtmosphereTransmittance", "AtmosphereSkySun", "AtmosphereSkySun2", "AtmosphereSkyMultiple", "AtmosphereSkySun2Output", "AtmosphereSkyMultipleOutput", "AerialSun2", "AerialMultiple", "AerialSun2Output", "AerialMultipleOutput", "AtmosphereSampler",
                "AtmosphereMultipleScattering", "AtmosphereMultipleScatteringOutput",
                "AtmosphereTransmittanceOutput", "AtmosphereSkySunOutput", "AerialSun", "AerialTransmission", "AerialSunOutput", "AerialTransmissionOutput", "FroxelSurfaceDepth"]);
            foreach (var program in programs) program.ValidateBindings(bindings);
            Parameters = ShaderParameters.Merge(programs.Select(p => p.Parameters).Concat(meteors == null ? [] : new[] { meteors.Geometry.Parameters, meteors.Surface.Parameters }));
            sampler = device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp, MaxLOD = 0,
            });
        }
        catch { Dispose(); throw; }
    }

    void Unbind()
    {
        Context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        Context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
        for (uint slot = 0; slot < 8; slot++) Context.CSSetUnorderedAccessView(slot, null);
    }

    internal override void Prepare(EffectFrame frame, ShaderParameters? parameters,
        Dictionary<string, ID3D11Buffer> buffers, Dictionary<string, ID3D11ShaderResourceView> textures,
        Dictionary<string, ID3D11SamplerState> samplers)
    {
        bool rebuildTransmittance = transmittance == null, rebuildSky = skySun == null;
        float[]? nextAtmosphere = atmosphereKey, nextSun = sunKey;
        if (!ReferenceEquals(previous, parameters))
        {
            // Only compare normalized values; all physical interpretation belongs to HLSL.
            var leaves = ShaderParameters.Leaves(parameters!.Items).ToArray();
            nextAtmosphere = leaves.Where(p => p.Path.StartsWith("Atmosphere.", StringComparison.Ordinal) && !p.Path.StartsWith("Atmosphere.Phase.", StringComparison.Ordinal))
                .Select(parameters.GetFloat).ToArray();
            nextSun = leaves.Where(p => p.Path.StartsWith("Sun.", StringComparison.Ordinal)
                || p.Path.StartsWith("Sun2.", StringComparison.Ordinal)
                || p.Path is "Transport.L1" or "Transport.L2Plus"
                || p.Path is "Camera.Alt" or "Camera.Long" or "Camera.Lat").Select(parameters.GetFloat).ToArray();
            rebuildTransmittance |= atmosphereKey == null || !nextAtmosphere.AsSpan().SequenceEqual(atmosphereKey);
            rebuildSky |= sunKey == null || !nextSun.AsSpan().SequenceEqual(sunKey);
        }
        rebuildSky |= rebuildTransmittance;
        transmittance ??= new(Device, TransmittanceWidth, TransmittanceHeight);
        skySun ??= new(Device, skyViewWidth, skyViewHeight);
        skySun2 ??= new(Device, skyViewWidth, skyViewHeight);
        skyMultiple ??= new(Device, skyViewWidth, skyViewHeight);
        textures["AtmosphereSkySun2"] = skySun2.View;
        textures["AtmosphereSkyMultiple"] = skyMultiple.View;
        outputs["AtmosphereSkySun2Output"] = skySun2.Output;
        outputs["AtmosphereSkyMultipleOutput"] = skyMultiple.Output;
        textures["AtmosphereTransmittance"] = transmittance.View;
        textures["AtmosphereSkySun"] = skySun.View;
        samplers["AtmosphereSampler"] = sampler;
        outputs["AtmosphereTransmittanceOutput"] = transmittance.Output;
        outputs["AtmosphereSkySunOutput"] = skySun.Output;
        if (rebuildTransmittance) Run(transmittanceProgram, "Atmosphere transmittance", TransmittanceWidth, TransmittanceHeight);
        multipleScattering ??= new(Device, 32, 32);
        textures["AtmosphereMultipleScattering"] = multipleScattering.View;
        outputs["AtmosphereMultipleScatteringOutput"] = multipleScattering.Output;
        multipleDirty |= rebuildTransmittance;
        var multipleToggle = ShaderParameters.Leaves(parameters!.Items).Single(p => p.Path == "Transport.L2Plus");
        bool showMultiple = ShaderParameters.Leaves(parameters.Items)
            .Any(p => (p.Path is "Debug.MultipleScattering" or "ShowMultipleScattering") && parameters.GetBoolean(p));
        if ((parameters.GetBoolean(multipleToggle) || showMultiple) && multipleDirty)
        {
            Run(multipleProgram, "Atmosphere multiple scattering", 32, 32);
            multipleDirty = false;
            rebuildSky |= parameters.GetBoolean(multipleToggle);
        }
        if (rebuildSky) Run(skyViewProgram, "Atmosphere sky view", skyViewWidth, skyViewHeight);
        if (aerialProgram != null)
        {
            var key = ReferenceEquals(previous, parameters) && aerialKey != null ? aerialKey : ShaderParameters.Leaves(parameters!.Items)
                .Where(p => (p.Path.StartsWith("Atmosphere.") && !p.Path.StartsWith("Atmosphere.Phase.")) || p.Path.StartsWith("Sun.") || p.Path.StartsWith("Sun2.")
                    || p.Path is "Transport.L1" or "Transport.L2Plus"
                    || p.Path.StartsWith("Camera.") || p.Path.StartsWith("View."))
                .Select(parameters.GetFloat).ToArray();
            float aspect = frame.Width / (float)frame.Height;
            if (AerialSize is < 2 or > 128) throw new InvalidOperationException("Aerial size must be between 2 and 128.");
            if (aerialSun != null && aerialSun.Texture.Description.Width != AerialSize)
            {
                Unbind();
                aerialSun.Dispose(); aerialSun2!.Dispose(); aerialMultiple!.Dispose(); aerialTransmission!.Dispose();
                aerialSun = null; aerialSun2 = null; aerialMultiple = null; aerialTransmission = null;
            }
            bool rebuild = aerialSun == null || aerialKey == null || !key.AsSpan().SequenceEqual(aerialKey) || aspect != aerialAspect;
            aerialSun ??= new(Device, AerialSize); aerialTransmission ??= new(Device, AerialSize);
            aerialSun2 ??= new(Device, AerialSize); aerialMultiple ??= new(Device, AerialSize);
            textures["AerialSun2"] = aerialSun2.View; textures["AerialMultiple"] = aerialMultiple.View;
            outputs["AerialSun2Output"] = aerialSun2.Output; outputs["AerialMultipleOutput"] = aerialMultiple.Output;
            textures["AerialSun"] = aerialSun.View;
            textures["AerialTransmission"] = aerialTransmission.View;
            outputs["AerialSunOutput"] = aerialSun.Output;
            outputs["AerialTransmissionOutput"] = aerialTransmission.Output;
            if (rebuild) Run(aerialProgram, "Atmosphere aerial perspective", AerialSize, AerialSize);
            aerialKey = key; aerialAspect = aspect;
            Unbind();
            var count = ShaderParameters.Leaves(parameters!.Items).Single(p => p.Path == "Shell.Count");
            meteors!.Draw(frame, parameters, (int)MathF.Round(4096 * parameters.GetFloat(count)), buffers, textures, samplers);
        }
        atmosphereKey = nextAtmosphere; sunKey = nextSun; previous = parameters;

        void Run(ShaderProgram program, string marker, int width, int height)
        {
            Unbind();
            frame.Marker(marker, true);
            try
            {
                program.Bind(Context, parameters, buffers, textures, samplers, outputs);
                Context.Dispatch((uint)((width + 7) / 8), (uint)((height + 7) / 8), 1);
            }
            finally { Unbind(); frame.Marker(marker, false); }
        }
    }

    internal override void Deactivate()
    {
        base.Deactivate(); outputs.Clear();
        multipleScattering?.Dispose(); multipleScattering = null; multipleDirty = true;
        skySun2?.Dispose(); skyMultiple?.Dispose(); skySun2 = null; skyMultiple = null;
        transmittance?.Dispose(); skySun?.Dispose(); transmittance = null; skySun = null;
        previous = null; atmosphereKey = null; sunKey = null; aerialKey = null;
        aerialSun?.Dispose(); aerialSun2?.Dispose(); aerialMultiple?.Dispose(); aerialTransmission?.Dispose();
        aerialSun = null; aerialSun2 = null; aerialMultiple = null; aerialTransmission = null;
        meteors?.Release();
    }
    public override void Dispose()
    {
        base.Dispose(); transmittanceProgram?.Dispose(); skyViewProgram?.Dispose(); multipleProgram?.Dispose(); aerialProgram?.Dispose(); meteors?.Dispose(); sampler?.Dispose();
    }

    sealed class Volume : IDisposable
    {
        internal readonly ID3D11Texture3D Texture;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11UnorderedAccessView Output;
        internal Volume(ID3D11Device device, int size)
        {
            Texture = device.CreateTexture3D(new Texture3DDescription {
                Width=(uint)size, Height=(uint)size, Depth=(uint)size, MipLevels=1, Format=Format.R16G16B16A16_Float,
                BindFlags=BindFlags.ShaderResource | BindFlags.UnorderedAccess });
            try { View=device.CreateShaderResourceView(Texture); Output=device.CreateUnorderedAccessView(Texture); }
            catch { View?.Dispose(); Texture.Dispose(); throw; }
        }
        public void Dispose() { Output.Dispose(); View.Dispose(); Texture.Dispose(); }
    }
    sealed class Lookup : IDisposable
    {
        internal readonly ID3D11Texture2D Texture;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11UnorderedAccessView Output;
        internal Lookup(ID3D11Device device, int width, int height)
        {
            Texture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, ArraySize = 1, MipLevels = 1,
                Format = Format.R32G32B32A32_Float, SampleDescription = new(1, 0),
                BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess,
            });
            try { View = device.CreateShaderResourceView(Texture); Output = device.CreateUnorderedAccessView(Texture); }
            catch { View?.Dispose(); Texture.Dispose(); throw; }
        }
        public void Dispose() { Output.Dispose(); View.Dispose(); Texture.Dispose(); }
    }
}

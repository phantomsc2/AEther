using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

/// <summary>Shared atmosphere LUT lifecycle for ground and orbital views.</summary>
public sealed class AtmosphereEffect : SceneEffect
{
    internal const int TransmittanceWidth = 256, TransmittanceHeight = 64;
    internal const int SkyViewWidth = 256, SkyViewHeight = 128;
    readonly ShaderProgram transmittanceProgram, skyViewProgram;
    readonly int skyViewWidth, skyViewHeight;
    readonly ID3D11SamplerState sampler;
    readonly Dictionary<string, ID3D11UnorderedAccessView> outputs = [];
    Lookup? transmittance, skyView;
    readonly ShaderProgram? aerialProgram;
    readonly FroxelRaster? meteors;
    Volume? aerialRadiance, aerialTransmission;
    float[]? aerialKey;
    float aerialAspect;
    // Internal override permits resolution comparisons without changing the public scene controls.
    internal int AerialSize { get; set; } = 32;
    ShaderParameters? previous;
    float[]? atmosphereKey, sunKey;

    // Internal texture access supports numerical GPU checks without display quantization.
    internal ID3D11Texture3D AerialRadianceTexture => aerialRadiance!.Texture;
    internal ID3D11Texture3D AerialTransmissionTexture => aerialTransmission!.Texture;
    internal ID3D11Texture2D TransmittanceTexture => transmittance!.Texture;
    internal ID3D11Texture2D SkyViewTexture => skyView!.Texture;

    internal AtmosphereEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file,
        int skyViewWidth = SkyViewWidth, int skyViewHeight = SkyViewHeight)
        : base(name, device, context, source, file)
    {
        this.skyViewWidth = skyViewWidth; this.skyViewHeight = skyViewHeight;
        try
        {
            transmittanceProgram = new(device, source, "BuildTransmittance", file, "cs_5_0");
            skyViewProgram = new(device, source, "BuildSkyView", file, "cs_5_0");
            if (file == "Planet.hlsl")
            {
                aerialProgram = new(device, source, "BuildAerial", file, "cs_5_0");
                meteors = new(device, context, source, file, marker: "Planet meteors");
            }
            var programs = new[] { Display, transmittanceProgram, skyViewProgram }
                .Concat(aerialProgram == null ? [] : new[] { aerialProgram }).ToArray();
            var bindings = FrameBindings.Concat(["AtmosphereTransmittance", "AtmosphereSkyView", "AtmosphereSampler",
                "AtmosphereTransmittanceOutput", "AtmosphereSkyViewOutput", "AerialRadiance", "AerialTransmission", "AerialRadianceOutput", "AerialTransmissionOutput", "FroxelSurfaceDepth"]);
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
        bool rebuildTransmittance = transmittance == null, rebuildSky = skyView == null;
        float[]? nextAtmosphere = atmosphereKey, nextSun = sunKey;
        if (!ReferenceEquals(previous, parameters))
        {
            // Only compare normalized values; all physical interpretation belongs to HLSL.
            var leaves = ShaderParameters.Leaves(parameters!.Items).ToArray();
            nextAtmosphere = leaves.Where(p => p.Path.StartsWith("Atmosphere.", StringComparison.Ordinal))
                .Select(parameters.GetFloat).ToArray();
            nextSun = leaves.Where(p => p.Path.StartsWith("Sun.", StringComparison.Ordinal)
                || p.Path.StartsWith("Sun2.", StringComparison.Ordinal)
                || p.Path.StartsWith("Phase.", StringComparison.Ordinal)
                || p.Path.StartsWith("Orbit.", StringComparison.Ordinal)).Select(parameters.GetFloat).ToArray();
            rebuildTransmittance |= atmosphereKey == null || !nextAtmosphere.AsSpan().SequenceEqual(atmosphereKey);
            rebuildSky |= sunKey == null || !nextSun.AsSpan().SequenceEqual(sunKey);
        }
        rebuildSky |= rebuildTransmittance;
        transmittance ??= new(Device, TransmittanceWidth, TransmittanceHeight);
        skyView ??= new(Device, skyViewWidth, skyViewHeight);
        textures["AtmosphereTransmittance"] = transmittance.View;
        textures["AtmosphereSkyView"] = skyView.View;
        samplers["AtmosphereSampler"] = sampler;
        outputs["AtmosphereTransmittanceOutput"] = transmittance.Output;
        outputs["AtmosphereSkyViewOutput"] = skyView.Output;
        if (rebuildTransmittance) Run(transmittanceProgram, "Atmosphere transmittance", TransmittanceWidth, TransmittanceHeight);
        if (rebuildSky) Run(skyViewProgram, "Atmosphere sky view", skyViewWidth, skyViewHeight);
        if (aerialProgram != null)
        {
            var key = ReferenceEquals(previous, parameters) && aerialKey != null ? aerialKey : ShaderParameters.Leaves(parameters!.Items)
                .Where(p => p.Path.StartsWith("Atmosphere.") || p.Path.StartsWith("Sun.") || p.Path.StartsWith("Sun2.")
                    || p.Path.StartsWith("Phase.") || p.Path.StartsWith("Orbit.") || p.Path.StartsWith("View."))
                .Select(parameters.GetFloat).ToArray();
            float aspect = frame.Width / (float)frame.Height;
            if (AerialSize is < 2 or > 128) throw new InvalidOperationException("Aerial size must be between 2 and 128.");
            if (aerialRadiance != null && aerialRadiance.Texture.Description.Width != AerialSize)
            {
                Unbind();
                aerialRadiance.Dispose(); aerialTransmission!.Dispose();
                aerialRadiance = null; aerialTransmission = null;
            }
            bool rebuild = aerialRadiance == null || aerialKey == null || !key.AsSpan().SequenceEqual(aerialKey) || aspect != aerialAspect;
            aerialRadiance ??= new(Device, AerialSize); aerialTransmission ??= new(Device, AerialSize);
            textures["AerialRadiance"] = aerialRadiance.View;
            textures["AerialTransmission"] = aerialTransmission.View;
            outputs["AerialRadianceOutput"] = aerialRadiance.Output;
            outputs["AerialTransmissionOutput"] = aerialTransmission.Output;
            if (rebuild) Run(aerialProgram, "Atmosphere aerial perspective", AerialSize, AerialSize);
            aerialKey = key; aerialAspect = aspect;
            Unbind();
            meteors!.Draw(frame, parameters, 1000, buffers, textures, samplers);
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
        transmittance?.Dispose(); skyView?.Dispose(); transmittance = null; skyView = null;
        previous = null; atmosphereKey = null; sunKey = null; aerialKey = null;
        aerialRadiance?.Dispose(); aerialTransmission?.Dispose();
        aerialRadiance = null; aerialTransmission = null;
        meteors?.Release();
    }
    public override void Dispose()
    {
        base.Dispose(); transmittanceProgram?.Dispose(); skyViewProgram?.Dispose(); aerialProgram?.Dispose(); meteors?.Dispose(); sampler?.Dispose();
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

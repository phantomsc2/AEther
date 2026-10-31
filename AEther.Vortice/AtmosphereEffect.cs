using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

/// <summary>Ground-level single-scattering sky. Integration runs only when a LUT's inputs change.</summary>
public sealed class AtmosphereEffect : SceneEffect
{
    internal const int TransmittanceWidth = 256, TransmittanceHeight = 64;
    internal const int SkyViewWidth = 256, SkyViewHeight = 128;
    readonly ShaderProgram transmittanceProgram, skyViewProgram;
    readonly ID3D11SamplerState sampler;
    readonly Dictionary<string, ID3D11UnorderedAccessView> outputs = [];
    Lookup? transmittance, skyView;
    ShaderParameters? previous;
    float[]? atmosphereKey, sunKey;

    // Internal texture access supports numerical GPU checks without display quantization.
    internal ID3D11Texture2D TransmittanceTexture => transmittance!.Texture;
    internal ID3D11Texture2D SkyViewTexture => skyView!.Texture;

    internal AtmosphereEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file)
        : base(name, device, context, source, file)
    {
        try
        {
            transmittanceProgram = new(device, source, "BuildTransmittance", file, "cs_5_0");
            skyViewProgram = new(device, source, "BuildSkyView", file, "cs_5_0");
            var programs = new[] { Display, transmittanceProgram, skyViewProgram };
            var bindings = FrameBindings.Concat(["AtmosphereTransmittance", "AtmosphereSkyView", "AtmosphereSampler",
                "AtmosphereTransmittanceOutput", "AtmosphereSkyViewOutput"]);
            foreach (var program in programs) program.ValidateBindings(bindings);
            Parameters = ShaderParameters.Merge(programs.Select(p => p.Parameters));
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
            nextSun = leaves.Where(p => p.Path is "Sun.Altitude" or "Sun.Intensity").Select(parameters.GetFloat).ToArray();
            rebuildTransmittance |= atmosphereKey == null || !nextAtmosphere.AsSpan().SequenceEqual(atmosphereKey);
            rebuildSky |= sunKey == null || !nextSun.AsSpan().SequenceEqual(sunKey);
        }
        rebuildSky |= rebuildTransmittance;
        transmittance ??= new(Device, TransmittanceWidth, TransmittanceHeight);
        skyView ??= new(Device, SkyViewWidth, SkyViewHeight);
        textures["AtmosphereTransmittance"] = transmittance.View;
        textures["AtmosphereSkyView"] = skyView.View;
        samplers["AtmosphereSampler"] = sampler;
        outputs["AtmosphereTransmittanceOutput"] = transmittance.Output;
        outputs["AtmosphereSkyViewOutput"] = skyView.Output;
        if (rebuildTransmittance) Run(transmittanceProgram, "Atmosphere transmittance", TransmittanceWidth, TransmittanceHeight);
        if (rebuildSky) Run(skyViewProgram, "Atmosphere sky view", SkyViewWidth, SkyViewHeight);
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
        previous = null; atmosphereKey = null; sunKey = null;
    }
    public override void Dispose()
    {
        base.Dispose(); transmittanceProgram?.Dispose(); skyViewProgram?.Dispose(); sampler?.Dispose();
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

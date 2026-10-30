using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

/// <summary>Adaptive spectrum-ring lighting shared by fog and raster surfaces.</summary>
public sealed class Froxel3Effect : FroxelEffect
{
    readonly ShaderProgram prefixProgram;
    PrefixStorage? prefix;
    public Froxel3Options CloudOptions
    {
        get { var value = Options; return new(value.DepthSlices, value.Instances, value.PointLights); }
        set => Options = new(value.DepthSlices, value.LightingArcs, value.Instances);
    }
    public Froxel3Options DefaultCloudOptions { get; } = new();
    public override object SizeOptions => CloudOptions;
    internal Froxel3Effect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file)
        : base(name, device, context, source, file, new(86, 8, 1024), customSource: true, enableRaster: true)
    {
        try
        {
            prefixProgram = new(device, source, "PrepareRingPrefix", file, "cs_5_0");
            prefixProgram.ValidateBindings(FrameBindings.Concat(["RingPrefixOutput"]));
        }
        catch { Dispose(); throw; }
    }
    protected override void RestoreSizes(object? sizes) { if (sizes is Froxel3Options value) CloudOptions = value; }
    protected override void ValidateOptions(FroxelOptions requested)
    {
        if (requested.PointLights is not (4 or 8 or 16 or 32))
            throw new InvalidOperationException("LightingArcs must be 4, 8, 16 or 32.");
    }
    internal override void Prepare(EffectFrame frame, ShaderParameters? parameters,
        Dictionary<string, ID3D11Buffer> buffers, Dictionary<string, ID3D11ShaderResourceView> textures,
        Dictionary<string, ID3D11SamplerState> samplers)
    {
        using var resource = textures["Spectrum0"].Resource;
        using var spectrum = resource.QueryInterface<ID3D11Texture2D>();
        uint bands = spectrum.Description.Width;
        Context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        Context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
        if (prefix == null || prefix.Bands != bands)
        {
            var next = new PrefixStorage(Device, bands);
            prefix?.Dispose(); prefix = next;
        }
        frame.Marker("Ring spectrum prefix", true);
        try
        {
            prefixProgram.Bind(Context, parameters, buffers, textures, samplers,
                new Dictionary<string, ID3D11UnorderedAccessView> { ["RingPrefixOutput"] = prefix.Output });
            Context.Dispatch(1, 2, 1);
        }
        finally
        {
            for (uint slot = 0; slot < 8; slot++) Context.CSSetUnorderedAccessView(slot, null);
            Context.CSSetShaderResources(0, ShaderProgram.EmptyViews);
            frame.Marker("Ring spectrum prefix", false);
        }
        textures["RingPrefix"] = prefix.View;
        base.Prepare(frame, parameters, buffers, textures, samplers);
    }
    internal override void Deactivate() { base.Deactivate(); prefix?.Dispose(); prefix = null; }
    public override void Dispose() { base.Dispose(); prefixProgram?.Dispose(); }

    sealed class PrefixStorage : IDisposable
    {
        internal readonly uint Bands;
        readonly ID3D11Texture2D texture;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11UnorderedAccessView Output;
        internal PrefixStorage(ID3D11Device device, uint bands)
        {
            Bands = bands;
            texture = device.CreateTexture2D(new Texture2DDescription {
                Width = bands + 1, Height = 2, ArraySize = 1, MipLevels = 1,
                Format = Format.R32G32B32A32_Float, SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess });
            try { View = device.CreateShaderResourceView(texture); Output = device.CreateUnorderedAccessView(texture); }
            catch { View?.Dispose(); Output?.Dispose(); texture.Dispose(); throw; }
        }
        public void Dispose() { View.Dispose(); Output.Dispose(); texture.Dispose(); }
    }
}

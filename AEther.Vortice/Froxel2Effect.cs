using Vortice.Direct3D11;

namespace AEther.Vortice;

/// <summary>Analytic spectrum-ring source using the shared camera-volume pipeline.</summary>
public sealed class Froxel2Effect : FroxelEffect
{
    public Froxel2Options DepthOptions
    {
        get => new(Options.DepthSlices);
        set => Options = new(value.DepthSlices, 0, 0);
    }
    public Froxel2Options DefaultDepthOptions { get; } = new();
    public override object SizeOptions => DepthOptions;
    internal Froxel2Effect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file)
        : base(name, device, context, source, file, new(86, 0, 0), customSource: true) { }
    protected override void RestoreSizes(object? sizes)
    {
        if (sizes is Froxel2Options value) DepthOptions = value;
    }
}

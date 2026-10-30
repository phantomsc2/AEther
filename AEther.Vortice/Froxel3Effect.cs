using Vortice.Direct3D11;
namespace AEther.Vortice;
public sealed class Froxel3Effect : FroxelEffect
{
    public Froxel3Options CloudOptions
    {
        get { var value = Options; return new(value.DepthSlices, value.Instances); }
        set => Options = new(value.DepthSlices, 0, value.Instances);
    }
    public Froxel3Options DefaultCloudOptions { get; } = new();
    public override object SizeOptions => CloudOptions;
    internal Froxel3Effect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file)
        : base(name, device, context, source, file, DefaultSizes(), customSource: true, enableRaster: true) { }
    static FroxelOptions DefaultSizes()
    {
        var sizes=new Froxel3Options();
        return new(sizes.DepthSlices,0,sizes.Instances);
    }
    protected override void RestoreSizes(object? sizes) { if (sizes is Froxel3Options value) CloudOptions = value; }
}

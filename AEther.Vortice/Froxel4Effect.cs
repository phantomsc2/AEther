using Vortice.Direct3D11;

namespace AEther.Vortice;

/// <summary>Audio-bin line emitters with parallel segment lighting and shell geometry.</summary>
public sealed class Froxel4Effect : FroxelEffect
{
    readonly ShaderProgram prepareProgram;
    LineStorage? lines;
    public Froxel4Options CloudOptions
    {
        get { var value=Options; return new(value.DepthSlices,value.Instances); }
        set => Options=new(value.DepthSlices,0,value.Instances);
    }
    public Froxel4Options DefaultCloudOptions { get; }=new();
    public override object SizeOptions => CloudOptions;
    internal int LineCount => lines?.Count ?? 0;
    internal Froxel4Effect(string name,ID3D11Device device,ID3D11DeviceContext context,string source,string file)
        : base(name,device,context,source,file,new(256,0,4096),customSource:true,enableRaster:true,
            preparedLighting:true,extraBindings:["LineLights"])
    {
        try
        {
            prepareProgram=new(device,source,"PrepareLineLights",file,"cs_5_0");
            prepareProgram.ValidateBindings(FrameBindings.Concat(["LineLightOutput"]));
            Parameters=ShaderParameters.Merge([Parameters,prepareProgram.Parameters]);
        }
        catch { Dispose(); throw; }
    }
    protected override void RestoreSizes(object? sizes) { if(sizes is Froxel4Options value) CloudOptions=value; }
    internal override void Prepare(EffectFrame frame,ShaderParameters? parameters,
        Dictionary<string,ID3D11Buffer> buffers,Dictionary<string,ID3D11ShaderResourceView> textures,
        Dictionary<string,ID3D11SamplerState> samplers)
    {
        using var resource=textures["Spectrum0"].Resource;
        using var spectrum=resource.QueryInterface<ID3D11Texture2D>();
        int count=checked((int)spectrum.Description.Width*2);
        Context.PSSetShaderResources(0,ShaderProgram.EmptyViews);
        Context.VSSetShaderResources(0,ShaderProgram.EmptyViews);
        Context.CSSetShaderResources(0,ShaderProgram.EmptyViews);
        if(lines==null || lines.Count!=count)
        {
            var next=new LineStorage(Device,count);lines?.Dispose();lines=next;
        }
        frame.Marker("Line light preparation",true);
        try
        {
            prepareProgram.Bind(Context,parameters,buffers,textures,samplers,
                new Dictionary<string,ID3D11UnorderedAccessView> {["LineLightOutput"]=lines.Output});
            Context.Dispatch((uint)(count+63)/64,1,1);
        }
        finally
        {
            for(uint slot=0;slot<8;slot++) Context.CSSetUnorderedAccessView(slot,null);
            Context.CSSetShaderResources(0,ShaderProgram.EmptyViews);
            frame.Marker("Line light preparation",false);
        }
        textures["LineLights"]=lines.View;
        base.Prepare(frame,parameters,buffers,textures,samplers);
    }
    internal override void Deactivate() { base.Deactivate();lines?.Dispose();lines=null; }
    public override void Dispose() { base.Dispose();prepareProgram?.Dispose(); }
    sealed class LineStorage : IDisposable
    {
        internal readonly int Count;
        readonly ID3D11Buffer buffer;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly ID3D11UnorderedAccessView Output;
        internal LineStorage(ID3D11Device device,int count)
        {
            Count=count;
            buffer=device.CreateBuffer(new BufferDescription {
                ByteWidth=checked((uint)(48*count)),StructureByteStride=48,Usage=ResourceUsage.Default,
                BindFlags=BindFlags.ShaderResource|BindFlags.UnorderedAccess,MiscFlags=ResourceOptionFlags.BufferStructured });
            try { View=device.CreateShaderResourceView(buffer);Output=device.CreateUnorderedAccessView(buffer); }
            catch { View?.Dispose();buffer.Dispose();throw; }
        }
        public void Dispose() { Output.Dispose();View.Dispose();buffer.Dispose(); }
    }
}

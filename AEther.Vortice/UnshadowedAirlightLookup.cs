using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// GPU resources only; the shader owns coefficients, validity and numerical work.
internal sealed class UnshadowedAirlightLookup : IDisposable
{
    internal const int Width = 513, Height = 1025;
    readonly List<IDisposable> owned = [];
    internal ID3D11ShaderResourceView View { get; }
    internal ID3D11ShaderResourceView State { get; }
    internal ID3D11UnorderedAccessView Output { get; }
    internal ID3D11UnorderedAccessView StateOutput { get; }
    internal ID3D11SamplerState Sampler { get; }
    internal UnshadowedAirlightLookup(ID3D11Device device, ID3D11DeviceContext context)
    {
        try
        {
            ID3D11Texture2D Texture(int width, int height) => Own(device.CreateTexture2D(new Texture2DDescription
            {
                Width=(uint)width, Height=(uint)height, ArraySize=1, MipLevels=1,
                Format=Format.R32G32B32A32_Float, SampleDescription=new(1,0),
                BindFlags=BindFlags.ShaderResource|BindFlags.UnorderedAccess,
            }));
            var table=Texture(Width,Height);
            View=Own(device.CreateShaderResourceView(table)); Output=Own(device.CreateUnorderedAccessView(table));
            var state=Texture(2,1);
            State=Own(device.CreateShaderResourceView(state)); StateOutput=Own(device.CreateUnorderedAccessView(state));
            Invalidate(context);
            Sampler=Own(device.CreateSamplerState(new SamplerDescription {
                Filter=Filter.MinMagLinearMipPoint, AddressU=TextureAddressMode.Clamp,
                AddressV=TextureAddressMode.Clamp, AddressW=TextureAddressMode.Clamp, MaxLOD=0,
            }));
        }
        catch { Dispose(); throw; }
    }
    T Own<T>(T value) where T:IDisposable { owned.Add(value); return value; }
    internal void Invalidate(ID3D11DeviceContext context) =>
        context.ClearUnorderedAccessView(StateOutput,new global::Vortice.Mathematics.Color4(0,0,0,0));
    public void Dispose() { for(int i=owned.Count-1;i>=0;i--) owned[i].Dispose(); owned.Clear(); }
}

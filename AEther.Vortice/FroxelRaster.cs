using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

// Full canvas resolution, independent of the fog's coarse screen grid.
internal sealed class FroxelRaster : IDisposable
{
    internal readonly ShaderProgram Geometry, Surface;
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly ID3D11DepthStencilState depthState;
    readonly ID3D11RasterizerState rasterState;
    Target? color, depth;
    internal FroxelRaster(ID3D11Device device, ID3D11DeviceContext context, string source, string file)
    {
        this.device=device; this.context=context;
        try
        {
            Geometry=new(device,source,"FroxelGeometryVS",file,"vs_5_0");
            Surface=new(device,source,"FroxelSurfacePS",file);
            var bindings=SceneEffect.FrameBindings.Concat(["FroxelConstants","FroxelLights","RingPrefix"]);
            Geometry.ValidateBindings(bindings); Surface.ValidateBindings(bindings);
            depthState=device.CreateDepthStencilState(new DepthStencilDescription {
                DepthEnable=true,DepthWriteMask=DepthWriteMask.All,DepthFunc=ComparisonFunction.Less,StencilEnable=false });
            rasterState=device.CreateRasterizerState(new RasterizerDescription {
                FillMode=FillMode.Solid,CullMode=CullMode.None,DepthClipEnable=true });
        }
        catch { Dispose(); throw; }
    }
    internal void Draw(EffectFrame frame, ShaderParameters? parameters, int instances,
        Dictionary<string,ID3D11Buffer> buffers, Dictionary<string,ID3D11ShaderResourceView> textures,
        Dictionary<string,ID3D11SamplerState> samplers)
    {
        if(color==null || color.Width!=frame.Width || color.Height!=frame.Height)
        {
            Target? nextColor=null,nextDepth=null;
            try { nextColor=new(device,frame.Width,frame.Height,false); nextDepth=new(device,frame.Width,frame.Height,true); }
            catch { nextColor?.Dispose(); nextDepth?.Dispose(); throw; }
            color?.Dispose(); depth?.Dispose(); color=nextColor; depth=nextDepth;
        }
        context.ClearRenderTargetView(color.Color!,new(0,0,0,0));
        context.ClearDepthStencilView(depth!.Depth!,DepthStencilClearFlags.Depth,1,0);
        context.RSSetViewport(0,0,frame.Width,frame.Height);
        context.RSSetState(rasterState);
        context.OMSetBlendState(null); context.OMSetDepthStencilState(depthState);
        context.OMSetRenderTargets(color.Color!,depth.Depth);
        try
        {
            Geometry.Bind(context,parameters,buffers,textures,samplers);
            Surface.Bind(context,parameters,buffers,textures,samplers);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            frame.Marker("Froxel raster",true);
            if(instances>0) context.DrawInstanced(24,(uint)instances,0,0);
            frame.Marker("Froxel raster",false);
        }
        finally
        {
            context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            context.OMSetDepthStencilState(null); context.RSSetState(null);
        }
        textures["FroxelSurface"]=color.View;
        textures["FroxelSurfaceDepth"]=depth.View;
    }
    sealed class Target : IDisposable
    {
        readonly ID3D11Texture2D texture;
        internal readonly ID3D11RenderTargetView? Color;
        internal readonly ID3D11DepthStencilView? Depth;
        internal readonly ID3D11ShaderResourceView View;
        internal readonly int Width,Height;
        internal Target(ID3D11Device device,int width,int height,bool depth)
        {
            Width=width; Height=height;
            texture=device.CreateTexture2D(new Texture2DDescription {
                Width=(uint)width,Height=(uint)height,ArraySize=1,MipLevels=1,
                Format=depth ? Format.R32_Typeless : Format.R16G16B16A16_Float,
                SampleDescription=new(1,0),Usage=ResourceUsage.Default,
                BindFlags=BindFlags.ShaderResource | (depth ? BindFlags.DepthStencil : BindFlags.RenderTarget) });
            try
            {
                if(depth) Depth=device.CreateDepthStencilView(texture,new DepthStencilViewDescription {
                    Format=Format.D32_Float,ViewDimension=DepthStencilViewDimension.Texture2D });
                else Color=device.CreateRenderTargetView(texture);
                View=device.CreateShaderResourceView(texture,new ShaderResourceViewDescription {
                    Format=depth ? Format.R32_Float : Format.R16G16B16A16_Float,
                    ViewDimension=ShaderResourceViewDimension.Texture2D,Texture2D=new Texture2DShaderResourceView { MipLevels=1 } });
            }
            catch { Color?.Dispose(); Depth?.Dispose(); texture.Dispose(); throw; }
        }
        public void Dispose() { View.Dispose(); Color?.Dispose(); Depth?.Dispose(); texture.Dispose(); }
    }
    internal void Release() { color?.Dispose(); depth?.Dispose(); color=null; depth=null; }
    public void Dispose() { Release(); Geometry?.Dispose(); Surface?.Dispose(); depthState?.Dispose(); rasterState?.Dispose(); }
}

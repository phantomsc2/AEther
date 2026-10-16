using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

public sealed partial class Renderer
{
    ID3D11Texture2D? geometryImage, geometryDepth;
    ID3D11RenderTargetView? geometryTarget;
    ID3D11ShaderResourceView? geometryView;
    ID3D11DepthStencilView? geometryDepthView;
    ID3D11DepthStencilState? geometryDepthState;
    ID3D11DepthStencilState? geometryNoDepthState;
    ID3D11BlendState? geometryAdditiveState;

    void ReleaseGeometryTargets()
    {
        namedTextures.Remove("SceneImage");
        geometryDepthView?.Dispose(); geometryDepthView = null;
        geometryView?.Dispose(); geometryView = null;
        geometryTarget?.Dispose(); geometryTarget = null;
        geometryImage?.Dispose(); geometryImage = null;
        geometryDepth?.Dispose(); geometryDepth = null;
    }

    void DrawGeometry(ShaderScene scene)
    {
        if (geometryImage == null)
        {
            try
            {
                geometryImage = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                    Format = Format.R16G16B16A16_Float, SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                });
                geometryTarget = device.CreateRenderTargetView(geometryImage);
                geometryView = device.CreateShaderResourceView(geometryImage);
                geometryDepth = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                    Format = Format.D32_Float, SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
                });
                geometryDepthView = device.CreateDepthStencilView(geometryDepth);
                geometryDepthState ??= device.CreateDepthStencilState(new DepthStencilDescription
                {
                    DepthEnable = true, DepthWriteMask = DepthWriteMask.All,
                    DepthFunc = ComparisonFunction.LessEqual, StencilEnable = false,
                });
            }
            catch { ReleaseGeometryTargets(); throw; }
        }
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.ClearRenderTargetView(geometryTarget!, new(0, 0, 0, 0));
        context.ClearDepthStencilView(geometryDepthView!, DepthStencilClearFlags.Depth, 1, 0);
        context.OMSetRenderTargets(geometryTarget!, geometryDepthView!);
        if (scene.GeometryAdditive)
        {
            geometryNoDepthState ??= device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.Always, StencilEnable = false,
            });
            if (geometryAdditiveState == null)
            {
                var blend = BlendDescription.Opaque;
                blend.RenderTarget[0] = new RenderTargetBlendDescription
                {
                    BlendEnable = true, SourceBlend = Blend.One, DestinationBlend = Blend.One,
                    BlendOperation = BlendOperation.Add, SourceBlendAlpha = Blend.One,
                    DestinationBlendAlpha = Blend.Zero, BlendOperationAlpha = BlendOperation.Add,
                    RenderTargetWriteMask = ColorWriteEnable.All,
                };
                geometryAdditiveState = device.CreateBlendState(blend);
            }
            context.OMSetDepthStencilState(geometryNoDepthState);
            context.OMSetBlendState(geometryAdditiveState);
        }
        else context.OMSetDepthStencilState(geometryDepthState);
        context.VSSetShader(scene.GeometryVertex!);
        scene.Program.Bind(context, scene.Parameters, namedBuffers, namedTextures, namedSamplers, vertexConstants: true);
        if (scene.GeometryInstanceCount > 1)
            context.DrawInstanced((uint)scene.GeometryVertexCount, (uint)scene.GeometryInstanceCount, 0, 0);
        else context.Draw((uint)scene.GeometryVertexCount, 0);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.OMSetDepthStencilState(null);
        context.OMSetBlendState(null);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShader(vertex);
        namedTextures["SceneImage"] = geometryView!;
    }
}

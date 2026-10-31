using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

/// <summary>Back-to-front premultiplied emission/absorption slabs.</summary>
public sealed class FractalCutEffect : SceneEffect
{
    readonly ShaderProgram slices, geometry;
    readonly ID3D11BlendState blend;
    readonly ID3D11DepthStencilState noDepth;
    ID3D11Texture2D? image;
    ID3D11RenderTargetView? target;
    ID3D11ShaderResourceView? view;

    internal FractalCutEffect(string name, ID3D11Device device, ID3D11DeviceContext context, string source, string file)
        : base(name, device, context, source, file)
    {
        try
        {
            geometry = new(device, source, "SliceVS", file, "vs_5_0");
            slices = new(device, source, "SlicePS", file);
            foreach (var program in new[] { Display, geometry, slices })
                program.ValidateBindings(FrameBindings.Concat(["SliceImage"]));
            Parameters = ShaderParameters.Merge([Display.Parameters, geometry.Parameters, slices.Parameters]);
            var description = BlendDescription.Opaque;
            description.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true, SourceBlend = Blend.One, DestinationBlend = Blend.InverseSourceAlpha,
                BlendOperation = BlendOperation.Add, SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteEnable.All,
            };
            blend = device.CreateBlendState(description);
            noDepth = device.CreateDepthStencilState(new DepthStencilDescription { DepthEnable = false, StencilEnable = false });
        }
        catch { Dispose(); throw; }
    }

    internal override unsafe void Prepare(EffectFrame frame, ShaderParameters? parameters,
        Dictionary<string, ID3D11Buffer> buffers, Dictionary<string, ID3D11ShaderResourceView> textures,
        Dictionary<string, ID3D11SamplerState> samplers)
    {
        Context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        if (image == null || image.Description.Width != frame.Width || image.Description.Height != frame.Height)
        {
            var next = Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)frame.Width, Height = (uint)frame.Height, MipLevels = 1, ArraySize = 1,
                Format = Format.R32G32B32A32_Float, SampleDescription = new(1, 0),
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            });
            ID3D11RenderTargetView? nextTarget = null;
            ID3D11ShaderResourceView? nextView = null;
            try { nextTarget = Device.CreateRenderTargetView(next); nextView = Device.CreateShaderResourceView(next); }
            catch { nextView?.Dispose(); nextTarget?.Dispose(); next.Dispose(); throw; }
            ReleaseImage(); image = next; target = nextTarget; view = nextView;
        }
        float* factor = stackalloc float[4];
        Context.OMGetBlendState(out var previousBlend, factor, out uint mask);
        Context.OMGetDepthStencilState(out var previousDepth, out uint stencil);
        frame.Marker("Julia volume slices", true);
        try
        {
            Context.ClearRenderTargetView(target!, new(0, 0, 0, 0));
            Context.OMSetRenderTargets(target!);
            Context.OMSetBlendState(blend);
            Context.OMSetDepthStencilState(noDepth);
            Context.RSSetViewport(0, 0, frame.Width, frame.Height);
            geometry.Bind(Context, parameters, buffers, textures, samplers);
            slices.Bind(Context, parameters, buffers, textures, samplers);
            // HLSL determines the active count. Excess instances are degenerate.
            Context.DrawInstanced(6, 256, 0, 0);
        }
        finally
        {
            Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            Context.OMSetBlendState(previousBlend, factor, mask);
            Context.OMSetDepthStencilState(previousDepth, stencil);
            previousBlend?.Dispose(); previousDepth?.Dispose();
            frame.Marker("Julia volume slices", false);
        }
        textures["SliceImage"] = view!;
    }

    // Unmapped linear RGBA for analytic transport checks, before tone mapping.
    internal float[] ReadVolumePixels()
    {
        var description = image!.Description;
        description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = Device.CreateTexture2D(description);
        Context.CopyResource(staging, image);
        var map = Context.Map(staging, 0, MapMode.Read, global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width = (int)description.Width, height = (int)description.Height;
            var values = new float[width * height * 4];
            for (int y = 0; y < height; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, values, y * width * 4, width * 4);
            return values;
        }
        finally { Context.Unmap(staging, 0); }
    }

    void ReleaseImage()
    {
        view?.Dispose(); target?.Dispose(); image?.Dispose();
        view = null; target = null; image = null;
    }
    internal override void Deactivate() { base.Deactivate(); ReleaseImage(); }
    public override void Dispose()
    {
        base.Dispose(); geometry?.Dispose(); slices?.Dispose(); blend?.Dispose(); noDepth?.Dispose();
    }
}

using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AEther.Vortice;

// Fixed-rate image iteration, with separate read/write targets and no readback.
internal sealed class ImageFeedback : IDisposable
{
    readonly ID3D11DeviceContext context;
    readonly MandelFluidSimulation.Surface[] images;
    int current;
    double pending;
    ShaderProgram? previous;
    internal ID3D11ShaderResourceView View => images[current].View;

    internal ImageFeedback(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
    {
        this.context = context;
        images = new MandelFluidSimulation.Surface[2];
        try
        {
            for (int i = 0; i < 2; i++)
                images[i] = new(device, width, height, Format.R16G16B16A16_Float);
            Clear();
        }
        catch { foreach (var image in images) image?.Dispose(); throw; }
    }

    internal void Clear()
    {
        foreach (var image in images) context.ClearRenderTargetView(image.Target, new Color4(0, 0, 0, 0));
        pending = 0;
    }

    internal void Advance(double dt, ShaderScene scene,
        IReadOnlyDictionary<string, ID3D11Buffer> buffers,
        Dictionary<string, ID3D11ShaderResourceView> textures,
        IReadOnlyDictionary<string, ID3D11SamplerState> samplers)
    {
        if (!ReferenceEquals(previous, scene.Program)) { Clear(); previous = scene.Program; }
        pending = Math.Min(pending + Math.Max(0, dt), 8.0 / 60);
        context.RSSetViewport(0, 0, images[0].Width, images[0].Height);
        while (pending + 1e-9 >= 1.0 / 60)
        {
            context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
            context.OMSetRenderTargets(images[1 - current].Target);
            textures["PreviousImage"] = View;
            scene.Program.Bind(context, scene.Parameters, buffers, textures, samplers);
            context.Draw(3, 0);
            current = 1 - current;
            pending -= 1.0 / 60;
        }
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        textures["PreviousImage"] = View;
    }

    public void Dispose() { foreach (var image in images) image.Dispose(); }
}

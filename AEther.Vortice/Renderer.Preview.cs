using System.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AEther.Vortice;

public sealed partial class Renderer
{
    ColorOutput? previewOutput;
    ID3D11PixelShader? previewPixel;
    int previewWidth, previewHeight;
    long nextPreview;
    bool previewCapacity;
    string? previewError;
    public bool HasPreviewOutput { get { lock (graphicsGate) return previewOutput != null; } }
    public string? PreviewOutputError { get { lock (graphicsGate) return previewError; } }
    internal long PreviewDrawCount { get; private set; }
    internal byte[] ReadPreviewPixels() { lock (graphicsGate) return previewOutput!.ReadPixels(false); }

    public void AttachPreviewOutput(IntPtr window, int width, int height, Action<IDXGISwapChain1?> attachSwapChain)
    {
        lock (graphicsGate)
        {
            DetachPreviewOutput();
            using var factory = global::Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            var candidate = new ColorOutput(device, context, factory, window, width, height, false, attachSwapChain,
                directSdr: true, logger: Logger);
            try
            {
                candidate.Resize(width, height);
                const string source = """
                    Texture2D<float4> Scene : register(t9);
                    float4 PS(float4 position : SV_POSITION, float2 uv : TEXCOORDS) : SV_Target
                    {
                        uint width, height; Scene.GetDimensions(width, height);
                        return float4(saturate(Scene.Load(int3(min(uint2(uv * float2(width, height)), uint2(width - 1, height - 1)), 0)).rgb), 1);
                    }
                    """;
                previewPixel = device.CreatePixelShader(Compiler.Compile(source, "PS", "Preview", "ps_5_0").Span);
                previewOutput = candidate;
                previewWidth = width; previewHeight = height;
                previewError = null;
            }
            catch { candidate.Dispose(); throw; }
        }
    }

    public void ResizePreviewOutput(int width, int height, float scaleX, float scaleY)
    {
        lock (graphicsGate)
        {
            if (previewOutput == null) return;
            if (width > 0 && height > 0 && (width != previewWidth || height != previewHeight))
            {
                previewOutput.Resize(width, height);
                previewWidth = width; previewHeight = height;
                previewCapacity = false;
            }
            previewOutput.SetCompositionScale(scaleX, scaleY);
        }
    }

    public void DetachPreviewOutput()
    {
        lock (graphicsGate)
        {
            previewOutput?.Dispose(); previewOutput = null;
            previewPixel?.Dispose(); previewPixel = null;
            previewCapacity = false; previewError = null; nextPreview = 0;
        }
    }

    bool DrawPreview(long timestamp, bool present)
    {
        if (previewOutput == null || previewError != null || timestamp < nextPreview) return false;
        nextPreview = timestamp + Stopwatch.Frequency / 10;
        try
        {
            if (present && !previewCapacity && !previewOutput.FrameReady.WaitOne(0)) return false;
            context.RSSetViewport(0, 0, previewWidth, previewHeight);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vertex);
            context.OMSetDepthStencilState(null);
            context.OMSetBlendState(null);
            context.OMSetRenderTargets(previewOutput.SceneTarget!);
            context.PSSetShaderResource(9, output.SceneView!);
            context.PSSetShader(previewPixel!);
            GpuMarker("Preview", true);
            try { context.Draw(3, 0); }
            finally { GpuMarker("Preview", false); }
            context.PSSetShaderResource(9, null!);
            PreviewDrawCount++;
            return true;
        }
        catch (Exception error)
        {
            context.PSSetShaderResource(9, null!);
            previewError = error.Message;
            Logger.Error(error, "Canvas preview rendering failed");
            return false;
        }
    }

    void PresentPreview()
    {
        try { previewCapacity = !previewOutput!.TryPresent(); }
        catch (Exception error) { previewError = error.Message; Logger.Error(error, "Canvas preview presentation failed"); }
    }
}

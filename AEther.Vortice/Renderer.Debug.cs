using Vortice.DXGI;

namespace AEther.Vortice;

public sealed partial class Renderer
{
    ColorOutput? debugOutput;
    int debugWidth, debugHeight;
    bool debugCapacity;
    string? debugOutputError;
    public bool HasDebugOutput { get { lock (graphicsGate) return debugOutput != null; } }
    public string? DebugOutputError { get { lock (graphicsGate) return debugOutputError; } }
    public WaitHandle? DebugFrameReady { get { lock (graphicsGate) return debugOutput?.FrameReady; } }
    internal long DebugDrawCount { get; private set; }

    /// <summary>Attach a second presentation surface sharing this renderer's audio and device.
    /// Low-cost previews render directly to SDR instead of a separate high-precision scene/color-conversion pass.</summary>
    public void AttachDebugOutput(IntPtr window, int width, int height, Action<IDXGISwapChain1?>? attachSwapChain = null,
        bool lowCost = false)
    {
        lock (graphicsGate)
        {
            DetachDebugOutput();
            using var factory = global::Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory2>();
            var candidate = new ColorOutput(device, context, factory, window, width, height,
                output.HighPrecision, attachSwapChain, directSdr: lowCost, logger: Logger);
            try { candidate.Resize(width, height); }
            catch { candidate.Dispose(); throw; }
            debugOutput = candidate;
            debugWidth = width; debugHeight = height;
            debugOutputError = null;
        }
    }

    public void ResizeDebugOutput(int width, int height, float scaleX = 1, float scaleY = 1)
    {
        lock (graphicsGate)
        {
            if (debugOutput == null) return;
            if (width > 0 && height > 0 && (width != debugWidth || height != debugHeight))
            {
                debugOutput.Resize(width, height);
                debugWidth = width; debugHeight = height;
            }
            debugOutput.SetCompositionScale(scaleX, scaleY);
        }
    }

    public void ClearDebugOutputError() { lock (graphicsGate) debugOutputError = null; }

    public void DetachDebugOutput()
    {
        lock (graphicsGate)
        {
            debugOutput?.Dispose(); debugOutput = null;
            debugCapacity = false; debugOutputError = null;
        }
    }

    internal byte[] ReadDebugPixels() { lock (graphicsGate) return debugOutput!.ReadPixels(false); }

    bool DrawDebug(Frame frame, double now, bool present, bool renderCanvas)
    {
        if (debugOutput == null || debugOutputError != null) return false;
        try
        {
            // The primary output's capacity is consumed by RenderWorker. A secondary
            // output must never make the primary wait for a slower/occluded window.
            if (present && renderCanvas && !debugCapacity && !debugOutput.FrameReady.WaitOne(0)) return false;
            if (!scenes.TryGetValue("Debug", out var shader)) throw new InvalidOperationException("Debug.hlsl is unavailable.");
            if (!shader.ProgramReady)
            {
                shader.EnsureCompiled();
                if (shader.Parameters != null && savedParameters.TryGetValue("Debug", out var saved)) shader.Parameters.Restore(saved);
            }
            frame.A.W = debugWidth / (float)debugHeight;
            frame.F.X = debugWidth; frame.F.Y = debugHeight;
            context.UpdateSubresource(frame, frameBuffer);
            context.RSSetViewport(0, 0, debugWidth, debugHeight);
            context.IASetPrimitiveTopology(global::Vortice.Direct3D.PrimitiveTopology.TriangleList);
            context.VSSetShader(vertex);
            context.OMSetDepthStencilState(null);
            context.OMSetBlendState(null);
            context.OMSetRenderTargets(debugOutput.SceneTarget!);
            shader.Program.Bind(context, shader.Parameters, namedBuffers, namedTextures, namedSamplers);
            context.Draw(3, 0);
            debugOutput.Draw(outputPixel, now);
            DebugDrawCount++;
            return true;
        }
        catch (Exception error)
        {
            debugOutputError = error.Message;
            Logger.Error(error, "Analysis preview rendering failed");
            return false;
        }
    }

    void PresentDebug(bool renderCanvas, uint syncInterval)
    {
        try
        {
            if (renderCanvas) debugCapacity = !debugOutput!.TryPresent();
            else { debugOutput!.Present(syncInterval); debugCapacity = false; }
        }
        catch (Exception error) { debugOutputError = error.Message; Logger.Error(error, "Analysis preview presentation failed"); }
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Serilog;

namespace AEther.Vortice;

/// <summary>High precision composition, with quantization confined to SDR fallback/export.</summary>
internal sealed partial class ColorOutput : IDisposable
{
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly IDXGISwapChain1 swapChain;
    readonly ID3D11Buffer constants;
    readonly IntPtr window;
    readonly Action<IDXGISwapChain1?>? attachSwapChain;
    readonly bool directSdr;
    ID3D11RenderTargetView? backTarget;
    ID3D11Texture2D? scene;
    ID3D11RenderTargetView? sceneTarget;
    ID3D11ShaderResourceView? sceneView;
    ID3D11Texture2D? export;
    ID3D11RenderTargetView? exportTarget;
    double nextDisplayCheck;
    float whiteScale = 1;
    float headroom = 1;
    float renderedGain = 1;
    int width, height;
    public bool HighPrecision { get; }
    public WaitHandle FrameReady { get; } = null!;
    public ID3D11RenderTargetView? SceneTarget => directSdr ? backTarget : sceneTarget;
    public string Description => directSdr ? "8-bit SDR" : HighPrecision ? "FP16 scRGB" : "8-bit SDR (dithered)";

    public ColorOutput(ID3D11Device device, ID3D11DeviceContext context, IDXGIFactory2 factory,
        IntPtr window, int width, int height, bool highPrecision, Action<IDXGISwapChain1?>? attachSwapChain = null,
        bool directSdr = false, ILogger? logger = null)
    {
        this.device = device; this.context = context;
        this.window = window; this.attachSwapChain = attachSwapChain;
        this.directSdr = directSdr;
        IDXGISwapChain1 Create(Format format)
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)Math.Max(1, width), Height = (uint)Math.Max(1, height),
                Format = format, BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new(1, 0),
                SwapEffect = attachSwapChain == null ? SwapEffect.FlipDiscard : SwapEffect.FlipSequential,
                Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.FrameLatencyWaitableObject,
            };
            return attachSwapChain == null
                ? factory.CreateSwapChainForHwnd(device, window, description)
                : factory.CreateSwapChainForComposition(device, description);
        }
        IDXGISwapChain1? candidate = null;
        try
        {
            if (highPrecision && !directSdr)
            {
                try
                {
                    candidate = Create(Format.R16G16B16A16_Float);
                    using var colorChain = candidate.QueryInterfaceOrNull<IDXGISwapChain3>();
                    if (colorChain != null)
                    {
                        var support = colorChain.CheckColorSpaceSupport(ColorSpaceType.RgbFullG10NoneP709);
                        if ((support & SwapChainColorSpaceSupportFlags.Present) == 0)
                            throw new NotSupportedException("scRGB presentation is unavailable.");
                        colorChain.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
                    }
                    HighPrecision = true;
                }
                catch (Exception error) when (error is SharpGenException or NotSupportedException)
                {
                    (logger ?? Log.Logger).ForContext<ColorOutput>().Warning(error, "High-precision presentation unavailable; falling back to SDR");
                    candidate?.Dispose(); candidate = null;
                    context.Flush();
                }
            }
            candidate ??= Create(Format.R8G8B8A8_UNorm);
            swapChain = candidate;
            (logger ?? Log.Logger).ForContext<ColorOutput>().Debug(
                "Presentation configured: {Output}; swap chain format {SwapChainFormat}; scene format {SceneFormat}; composition {Composition}; dimensions {Width} x {Height}",
                Description, HighPrecision ? Format.R16G16B16A16_Float : Format.R8G8B8A8_UNorm,
                directSdr ? Format.R8G8B8A8_UNorm : Format.R16G16B16A16_Float, attachSwapChain != null, width, height);
            using (var chain = swapChain.QueryInterface<IDXGISwapChain2>())
            {
                chain.MaximumFrameLatency = 1;
                var handle = chain.FrameLatencyWaitableObject;
                if (handle == IntPtr.Zero)
                    throw new InvalidOperationException("The swap chain did not provide a frame latency handle.");
                FrameReady = new FrameWaitHandle(handle);
            }
            constants = device.CreateBuffer(32, BindFlags.ConstantBuffer);
            attachSwapChain?.Invoke(swapChain);
        }
        catch
        {
            FrameReady?.Dispose();
            candidate?.Dispose();
            ReleaseTargets(); constants?.Dispose();
            throw;
        }
    }

    public void SetCompositionScale(float x, float y)
    {
        if (attachSwapChain == null) return;
        if (!float.IsFinite(x) || !float.IsFinite(y) || x <= 0 || y <= 0)
            throw new ArgumentOutOfRangeException(nameof(x));
        using var chain = swapChain.QueryInterface<IDXGISwapChain2>();
        chain.MatrixTransform = Matrix3x2.CreateScale(1 / x, 1 / y);
    }

    public void Resize(int newWidth, int newHeight)
    {
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        context.PSSetShaderResource(9, null!);
        ReleaseTargets();
        swapChain.ResizeBuffers(2, (uint)newWidth, (uint)newHeight, Format.Unknown, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
        using var back = swapChain.GetBuffer<ID3D11Texture2D>(0);
        backTarget = device.CreateRenderTargetView(back);
        width = newWidth; height = newHeight;
        nextDisplayCheck = 0;
        // Diagnostic previews can draw display-encoded colors straight into the SDR back buffer.
        if (directSdr) return;
        // Keep linear illumination and display colors at high precision until mapping.
        scene = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)newWidth, Height = (uint)newHeight, MipLevels = 1, ArraySize = 1,
            Format = Format.R16G16B16A16_Float, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        sceneTarget = device.CreateRenderTargetView(scene);
        sceneView = device.CreateShaderResourceView(scene);
    }

    public void Draw(ID3D11PixelShader shader, double now, double exposure = 0)
    {
        if (directSdr) return;
        renderedGain = (float)Math.Pow(2, exposure);
        if (HighPrecision && now >= nextDisplayCheck)
        {
            nextDisplayCheck = now + 1;
            whiteScale = headroom = 1;
            // Poll infrequently to follow monitor moves, HDR toggles and the
            // Windows SDR-brightness slider without doing display queries per frame.
            try
            {
                using var output = FindOutput();
                using var advanced = output?.QueryInterfaceOrNull<IDXGIOutput6>();
                if (advanced?.Description1 is { ColorSpace: ColorSpaceType.RgbFullG2084NoneP2020 } display)
                {
                    whiteScale = DisplayWhiteLevel.GetScale(output!.Description.DeviceName);
                    if (float.IsFinite(display.MaxLuminance) && display.MaxLuminance > 0)
                        headroom = Math.Max(1, display.MaxLuminance / (80 * whiteScale));
                }
            }
            catch (SharpGenException) { whiteScale = headroom = 1; }
        }
        DrawTo(shader, backTarget!, new(HighPrecision ? 1 : 0, whiteScale, renderedGain, headroom), true);
    }

    void DrawTo(ID3D11PixelShader shader, ID3D11RenderTargetView target, Vector4 mapping, bool dither)
    {
        context.UpdateSubresource(new Presentation { Mapping = mapping, Dither = new(dither ? 1 : 0, 0, 0, 0) }, constants);
        context.PSSetConstantBuffer(3, constants);
        context.OMSetBlendState(null);
        context.OMSetDepthStencilState(null);
        context.OMSetRenderTargets(target);
        context.PSSetShaderResource(9, sceneView!);
        context.PSSetShader(shader);
        context.Draw(3, 0);
        context.PSSetShaderResource(9, null!);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Presentation { public Vector4 Mapping, Dither; }

    IDXGIOutput? FindOutput()
    {
        if (attachSwapChain == null)
            return swapChain.GetContainingOutput(out var output).Success ? output : null;
        // Composition swap chains have no HWND and don't support GetContainingOutput.
        // Resolve the owning window's monitor across adapters (including hybrid GPUs).
        var monitor = MonitorFromWindow(window, 2);
        using var factory = global::Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
                for (uint o = 0; adapter.EnumOutputs(o, out var candidate).Success; o++)
                {
                    if (candidate.Description.Monitor == monitor) return candidate;
                    candidate.Dispose();
                }
        }
        return null;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    public void Present(uint syncInterval) => swapChain.Present(syncInterval, PresentFlags.None).CheckError();

    public bool TryPresent()
    {
        var result = swapChain.Present(0, PresentFlags.DoNotWait);
        if (result.Code == unchecked((int)0x887A000A)) return false; // DXGI_ERROR_WAS_STILL_DRAWING
        result.CheckError();
        return true;
    }

    /// <summary>Display-encoded RGBA8 for PNGs/tests, before monitor-specific white scaling.</summary>
    public byte[] ReadPixels(ID3D11VertexShader vertex, ID3D11PixelShader shader, bool dither)
    {
        if (directSdr)
        {
            using var back = swapChain.GetBuffer<ID3D11Texture2D>(0);
            return ReadSurface(back, 4);
        }
        if (export == null)
        {
            var description = scene!.Description;
            description.Format = Format.R8G8B8A8_UNorm;
            description.BindFlags = BindFlags.RenderTarget;
            export = device.CreateTexture2D(description);
            exportTarget = device.CreateRenderTargetView(export);
        }
        // Reuse the presentation shader, forcing SDR independently of the monitor.
        // The gain belongs to the last frame, even if options changed since drawing.
        context.RSSetViewport(0, 0, width, height);
        context.IASetPrimitiveTopology(global::Vortice.Direct3D.PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        DrawTo(shader, exportTarget!, new(0, 1, renderedGain, 1), dither);
        return ReadSurface(export, 4);
    }

    internal float[] ReadPresentedPixels()
    {
        using var back = swapChain.GetBuffer<ID3D11Texture2D>(0);
        var bytes = ReadSurface(back, HighPrecision ? 8 : 4);
        return HighPrecision ? MemoryMarshal.Cast<byte, Half>(bytes).ToArray().Select(v => (float)v).ToArray()
            : bytes.Select(v => v / 255f).ToArray();
    }

    byte[] ReadSurface(ID3D11Texture2D texture, int bytesPerPixel)
    {
        var desc = texture.Description;
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, texture);
        var map = context.Map(staging, 0, MapMode.Read, global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            var result = new byte[width * height * bytesPerPixel];
            for (int y = 0; y < height; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, result, y * width * bytesPerPixel, width * bytesPerPixel);
            return result;
        }
        finally { context.Unmap(staging, 0); }
    }

    void ReleaseTargets()
    {
        exportTarget?.Dispose(); exportTarget = null;
        export?.Dispose(); export = null;
        sceneView?.Dispose(); sceneView = null;
        sceneTarget?.Dispose(); sceneTarget = null;
        scene?.Dispose(); scene = null;
        backTarget?.Dispose(); backTarget = null;
    }
    public void Dispose()
    {
        try { attachSwapChain?.Invoke(null); }
        finally { ReleaseTargets(); constants.Dispose(); FrameReady.Dispose(); swapChain.Dispose(); }
    }

    sealed class FrameWaitHandle : WaitHandle
    {
        public FrameWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

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
    ID3D11RenderTargetView? backTarget;
    ID3D11Texture2D? scene;
    ID3D11RenderTargetView? sceneTarget;
    ID3D11ShaderResourceView? sceneView;
    double nextDisplayCheck;
    float whiteScale = 1;
    int width, height;
    public bool HighPrecision { get; }
    public ID3D11RenderTargetView? SceneTarget => sceneTarget;
    public string Description => HighPrecision ? "FP16 scRGB" : "8-bit SDR (dithered)";

    public ColorOutput(ID3D11Device device, ID3D11DeviceContext context, IDXGIFactory2 factory,
        IntPtr window, int width, int height, bool highPrecision, Action<IDXGISwapChain1?>? attachSwapChain = null)
    {
        this.device = device; this.context = context;
        this.window = window; this.attachSwapChain = attachSwapChain;
        IDXGISwapChain1 Create(Format format)
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)Math.Max(1, width), Height = (uint)Math.Max(1, height),
                Format = format, BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new(1, 0),
                SwapEffect = attachSwapChain == null ? SwapEffect.FlipDiscard : SwapEffect.FlipSequential,
                Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore,
            };
            return attachSwapChain == null
                ? factory.CreateSwapChainForHwnd(device, window, description)
                : factory.CreateSwapChainForComposition(device, description);
        }
        IDXGISwapChain1? candidate = null;
        try
        {
            if (highPrecision)
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
                    candidate?.Dispose(); candidate = null;
                    context.Flush();
                }
            }
            candidate ??= Create(Format.R8G8B8A8_UNorm);
            swapChain = candidate;
            constants = device.CreateBuffer(16, BindFlags.ConstantBuffer);
            attachSwapChain?.Invoke(swapChain);
        }
        catch
        {
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
        swapChain.ResizeBuffers(2, (uint)newWidth, (uint)newHeight, Format.Unknown, SwapChainFlags.None).CheckError();
        using var back = swapChain.GetBuffer<ID3D11Texture2D>(0);
        backTarget = device.CreateRenderTargetView(back);
        // Never quantize the scene to 8 bits before decoding for scRGB.
        scene = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)newWidth, Height = (uint)newHeight, MipLevels = 1, ArraySize = 1,
            Format = Format.R16G16B16A16_Float, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        sceneTarget = device.CreateRenderTargetView(scene);
        sceneView = device.CreateShaderResourceView(scene);
        width = newWidth; height = newHeight;
        nextDisplayCheck = 0;
    }

    public void Draw(ID3D11PixelShader shader, double now)
    {
        if (HighPrecision && now >= nextDisplayCheck)
        {
            nextDisplayCheck = now + 1;
            whiteScale = 1;
            // Poll infrequently to follow monitor moves, HDR toggles and the
            // Windows SDR-brightness slider without doing display queries per frame.
            using var output = FindOutput();
            if (output != null)
            {
                using (var advanced = output.QueryInterfaceOrNull<IDXGIOutput6>())
                    if (advanced?.Description1.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020)
                        whiteScale = DisplayWhiteLevel.GetScale(output.Description.DeviceName);
            }
        }
        context.UpdateSubresource(new Vector4(HighPrecision ? 1 : 0, whiteScale, 0, 0), constants);
        context.PSSetConstantBuffer(3, constants);
        context.OMSetRenderTargets(backTarget!);
        context.PSSetShaderResource(9, sceneView!);
        context.PSSetShader(shader);
        context.Draw(3, 0);
        context.PSSetShaderResource(9, null!);
    }

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

    public void Present() => swapChain.Present(1, PresentFlags.None).CheckError();

    /// <summary>Display-encoded RGBA8 for PNGs/tests, before monitor-specific white scaling.</summary>
    public byte[] ReadPixels(bool dither)
    {
        var values = MemoryMarshal.Cast<byte, Half>(ReadSurface(scene!, 8));
        var result = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            for (int c = 0; c < 3; c++)
            {
                float encoded = Math.Clamp((float)values[4 * pixel + c], 0, 1);
                float noise = dither ? (Hash((uint)(3 * pixel + c)) >> 8) / 16777216f - .5f : 0;
                result[4 * pixel + c] = (byte)Math.Clamp((int)MathF.Floor(encoded * 255 + noise + .5f), 0, 255);
            }
            result[4 * pixel + 3] = 255;
        }
        return result;
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

    static uint Hash(uint value)
    {
        unchecked
        {
            value ^= value >> 16; value *= 0x7feb352d;
            value ^= value >> 15; value *= 0x846ca68b;
            return value ^ (value >> 16);
        }
    }
    void ReleaseTargets()
    {
        sceneView?.Dispose(); sceneView = null;
        sceneTarget?.Dispose(); sceneTarget = null;
        scene?.Dispose(); scene = null;
        backTarget?.Dispose(); backTarget = null;
    }
    public void Dispose()
    {
        try { attachSwapChain?.Invoke(null); }
        finally { ReleaseTargets(); constants.Dispose(); swapChain.Dispose(); }
    }
}

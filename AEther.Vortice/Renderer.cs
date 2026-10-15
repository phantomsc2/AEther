using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace AEther.Vortice;

public enum Scene { Spectrum, Histogram, Mandelbox }
public enum AntialiasingQuality { None, Medium, High }

/// <summary>D3D11 fullscreen passes. All methods except PostAudio run on the window thread.</summary>
public sealed class Renderer : IDisposable
{
    readonly List<IDisposable> resources = [];
    ID3D11Device device = null!;
    ID3D11DeviceContext context = null!;
    IDXGISwapChain1 swapChain = null!;
    ID3D11RenderTargetView? target;
    ID3D11VertexShader vertex = null!;
    ID3D11PixelShader[] pixels = [];
    ID3D11PixelShader? bilateral;
    readonly ID3D11ShaderResourceView[] fractalViews = new ID3D11ShaderResourceView[4];
    readonly ID3D11RenderTargetView[] filteredTargets = new ID3D11RenderTargetView[2];
    ID3D11Buffer frameBuffer = null!, sceneBuffer = null!;
    readonly ID3D11Texture2D[] textures = new ID3D11Texture2D[4];
    readonly ID3D11ShaderResourceView[] views = new ID3D11ShaderResourceView[4];
    readonly string shaderDirectory;
    int width, height;
    double previousTime;
    double previousMusicalTime, movementTime;
    bool disposed;
    long previousFrame;
    public AudioHistory Audio { get; }
    public Scene Scene { get; set; } = Scene.Mandelbox;
    public bool CombinedLevel { get; set; }
    public bool Intensity { get; set; }
    public float SliceDepth { get; set; }
    public AntialiasingQuality MandelboxAntialiasing { get; set; } = AntialiasingQuality.Medium;
    public int MandelboxIterations { get; set; } = 10;
    public float MandelboxMovementSpeed { get; set; } = 1;
    public float MandelboxScale { get; set; } = -1.5f;
    public float BilateralStrength { get; set; } = .6f;
    public float BilateralFrequencySigma { get; set; } = 1;
    public float BilateralTimeSigma { get; set; } = 1;
    public float BilateralRangeSigma { get; set; } = .1f;
    public bool FilteredHistogramView { get; set; }
    public string? ShaderError { get; private set; }
    public string ShaderDirectory => shaderDirectory;

    T Own<T>(T value) where T : IDisposable { resources.Add(value); return value; }

    public Renderer(IntPtr window, int width, int height, int bands, double analysisRate,
        double historySeconds = 1, string? shaderDirectory = null)
    {
        Audio = new(bands, analysisRate, historySeconds);
        this.shaderDirectory = shaderDirectory ?? FindShaderDirectory();
        try
        {
            D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out device, out context).CheckError();
            Own(device); Own(context);
            using var factory = CreateDXGIFactory1<IDXGIFactory2>();
            swapChain = Own(factory.CreateSwapChainForHwnd(device, window, new SwapChainDescription1
            {
                Width = (uint)Math.Max(1, width), Height = (uint)Math.Max(1, height),
                Format = Format.R8G8B8A8_UNorm, BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
            }));
            factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter).CheckError();
            frameBuffer = Own(device.CreateBuffer(80, BindFlags.ConstantBuffer));
            sceneBuffer = Own(device.CreateBuffer(64, BindFlags.ConstantBuffer));
            for (var i = 0; i < 4; i++)
            {
                textures[i] = Own(device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)bands, Height = (uint)(i < 2 ? 1 : Audio.Rows),
                    MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                }));
                views[i] = Own(device.CreateShaderResourceView(textures[i]));
                if (i < 2) fractalViews[i] = views[i];
                else
                {
                    var desc = textures[i].Description;
                    desc.BindFlags |= BindFlags.RenderTarget;
                    var filtered = Own(device.CreateTexture2D(desc));
                    fractalViews[i] = Own(device.CreateShaderResourceView(filtered));
                    filteredTargets[i - 2] = Own(device.CreateRenderTargetView(filtered));
                }
            }
            var sampler = Own(device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagLinearMipPoint, AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue, ComparisonFunc = ComparisonFunction.Never,
            }));
            context.PSSetSampler(0, sampler);
            var rasterizer = Own(device.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true,
            }));
            context.RSSetState(rasterizer);
            Resize(width, height);
            if (!ReloadShaders()) throw new InvalidOperationException(ShaderError);
        }
        catch { Dispose(); throw; }
    }

    public void PostAudio(SampleEvent<double> input, int channelCount) => Audio.Post(input, channelCount);

    static string FindShaderDirectory()
    {
        // Prefer editable source files when running from Rider or the repository.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "AEther.Vortice", "Shaders");
                if (File.Exists(Path.Combine(candidate, "Common.hlsl"))) return candidate;
            }
        return Path.Combine(AppContext.BaseDirectory, "Shaders");
    }

    public bool ReloadShaders()
    {
        ID3D11VertexShader? nextVertex = null;
        ID3D11PixelShader? nextBilateral = null;
        var nextPixels = new List<ID3D11PixelShader>();
        try
        {
            var common = File.ReadAllText(Path.Combine(shaderDirectory, "Common.hlsl"));
            nextVertex = device.CreateVertexShader(Compiler.Compile(common, "VS", "Common.hlsl", "vs_5_0").Span);
            foreach (var scene in Enum.GetValues<Scene>())
            {
                var name = scene + ".hlsl";
                var source = common + "\n#line 1 \"" + name + "\"\n" + File.ReadAllText(Path.Combine(shaderDirectory, name));
                nextPixels.Add(device.CreatePixelShader(Compiler.Compile(source, "PS", name, "ps_5_0").Span));
            }
            var filterSource = common + "\n" + File.ReadAllText(Path.Combine(shaderDirectory, "Bilateral.hlsl"));
            nextBilateral = device.CreatePixelShader(Compiler.Compile(filterSource, "PS", "Bilateral.hlsl", "ps_5_0").Span);
            foreach (var pixel in pixels) pixel.Dispose();
            bilateral?.Dispose();
            bilateral = nextBilateral;
            vertex?.Dispose();
            vertex = nextVertex;
            pixels = nextPixels.ToArray();
            ShaderError = null;
            return true;
        }
        catch (Exception error)
        {
            nextVertex?.Dispose();
            nextBilateral?.Dispose();
            foreach (var pixel in nextPixels) pixel.Dispose();
            ShaderError = error.Message;
            return false;
        }
    }

    public void Resize(int newWidth, int newHeight)
    {
        if (newWidth < 1 || newHeight < 1 || newWidth == width && newHeight == height) return;
        context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        target?.Dispose(); target = null;
        swapChain.ResizeBuffers(2, (uint)newWidth, (uint)newHeight, Format.Unknown, SwapChainFlags.None).CheckError();
        using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        target = device.CreateRenderTargetView(backBuffer);
        width = newWidth; height = newHeight;
    }

    public unsafe void Render(bool present = true)
    {
        if (target == null) return;
        var frameStart = Stopwatch.GetTimestamp();
        if (previousFrame != 0) Audio.Profile?.Record("render.frame_interval_ms", Stopwatch.GetElapsedTime(previousFrame, frameStart).TotalMilliseconds);
        previousFrame = frameStart;
        var now = Audio.Now;
        Audio.Advance(Math.Max(0, now - previousTime));
        movementTime += (Audio.MusicalTime - previousMusicalTime) * Math.Max(0, MandelboxMovementSpeed);
        previousMusicalTime = Audio.MusicalTime;
        previousTime = now;
        // Upload after draining the mailbox; capture/analysis never touches a graphics lock.
        context.PSSetShaderResources(0, new ID3D11ShaderResourceView[4]);
        for (var i = 0; i < 4; i++)
        {
            var source = i < 2 ? Audio.Spectrum[i] : Audio.History[i - 2];
            fixed (float* data = source)
                context.UpdateSubresource(textures[i], 0, null, (IntPtr)data, (uint)(Audio.Bands * 16), 0);
        }
        var frame = new Frame
        {
            A = new((float)now, 0, Audio.Shift, width / (float)height),
            B = new(Audio.Flash, 0, 0, Audio.TimeScale),
            C = new(0, 0, 0, Audio.Energy),
            D = new(Audio.Energy, Audio.Flash, Audio.Flash * .3f, Audio.Energy),
            E = new((float)Audio.MusicalTime, Audio.AvailableSpan, Audio.NewestAge,
                Audio.FractalDepth * Math.Max(1, MandelboxIterations) / 10f),
        };
        context.UpdateSubresource(frame, frameBuffer);
        var sceneParameters = new SceneParameters
        {
            A = new(SliceDepth, .95f, 4, (CombinedLevel ? 1 : 0) + (Intensity ? 2 : 0)),
            B = new((int)MandelboxAntialiasing, (float)movementTime, 0, 0),
            C = new(MandelboxScale, 0, 0, 0),
            D = new(Math.Clamp(BilateralFrequencySigma, .1f, 2), Math.Clamp(BilateralTimeSigma, .1f, 2),
                Math.Max(.0001f, BilateralRangeSigma), Math.Clamp(BilateralStrength, 0, 1)),
        };
        context.UpdateSubresource(sceneParameters, sceneBuffer);
        context.RSSetViewport(0, 0, width, height);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        context.PSSetConstantBuffer(0, frameBuffer);
        context.PSSetConstantBuffer(1, sceneBuffer);
        context.PSSetShaderResources(0, views);
        var filter = (Scene == Scene.Mandelbox || Scene == Scene.Histogram && FilteredHistogramView)
            && BilateralStrength > 0;
        if (filter)
        {
            // Filter once in history space, shared by all fractal/AA samples.
            context.RSSetViewport(0, 0, Audio.Bands, Audio.Rows);
            context.OMSetRenderTargets(filteredTargets);
            context.PSSetShader(bilateral);
            context.Draw(3, 0);
            context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
            context.PSSetShaderResources(0, fractalViews);
            context.RSSetViewport(0, 0, width, height);
        }
        context.OMSetRenderTargets(target);
        context.PSSetShader(pixels[(int)Scene]);
        context.Draw(3, 0);
        Audio.Profile?.Elapsed("render.draw_cpu_ms", frameStart);
        if (Audio.LatestCaptureTimestamp != 0)
            Audio.Profile?.Elapsed("render.capture_to_submission_ms", Audio.LatestCaptureTimestamp);
        var presentStart = Stopwatch.GetTimestamp();
        if (present) swapChain.Present(1, PresentFlags.None).CheckError();
        if (present) Audio.Profile?.Elapsed("render.present_wait_ms", presentStart);
    }

    /// <summary>RGBA rows for deterministic render checks; call after Render(false).</summary>
    public byte[] ReadPixels()
    {
        using var back = swapChain.GetBuffer<ID3D11Texture2D>(0);
        var desc = back.Description;
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, back);
        var map = context.Map(staging, 0, MapMode.Read, global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            var result = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, result, y * width * 4, width * 4);
            return result;
        }
        finally { context.Unmap(staging, 0); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Frame { public Vector4 A, B, C, D, E; }
    [StructLayout(LayoutKind.Sequential)]
    struct SceneParameters { public Vector4 A, B, C, D; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        context?.ClearState(); context?.Flush();
        target?.Dispose(); vertex?.Dispose();
        bilateral?.Dispose();
        foreach (var pixel in pixels) pixel.Dispose();
        for (var i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
    }
}

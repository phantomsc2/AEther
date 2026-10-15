using System.Numerics;
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
    ID3D11Buffer frameBuffer = null!, sceneBuffer = null!;
    readonly ID3D11Texture2D[] textures = new ID3D11Texture2D[4];
    readonly ID3D11ShaderResourceView[] views = new ID3D11ShaderResourceView[4];
    readonly string shaderDirectory;
    int width, height;
    double previousTime;
    bool disposed;
    public AudioHistory Audio { get; }
    public Scene Scene { get; set; } = Scene.Mandelbox;
    public bool CombinedLevel { get; set; }
    public bool Intensity { get; set; }
    public float SliceDepth { get; set; }
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
            sceneBuffer = Own(device.CreateBuffer(16, BindFlags.ConstantBuffer));
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
            foreach (var pixel in pixels) pixel.Dispose();
            vertex?.Dispose();
            vertex = nextVertex;
            pixels = nextPixels.ToArray();
            ShaderError = null;
            return true;
        }
        catch (Exception error)
        {
            nextVertex?.Dispose();
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
        var now = Audio.Now;
        Audio.Advance(Math.Max(0, now - previousTime));
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
            E = new((float)Audio.MusicalTime, 0, 0, 0),
        };
        context.UpdateSubresource(frame, frameBuffer);
        var sceneParameters = new Vector4(SliceDepth, .7f, 4,
            (CombinedLevel ? 1 : 0) + (Intensity ? 2 : 0));
        context.UpdateSubresource(sceneParameters, sceneBuffer);
        context.OMSetRenderTargets(target);
        context.RSSetViewport(0, 0, width, height);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);
        context.PSSetShader(pixels[(int)Scene]);
        context.PSSetConstantBuffer(0, frameBuffer);
        context.PSSetConstantBuffer(1, sceneBuffer);
        context.PSSetShaderResources(0, views);
        context.Draw(3, 0);
        if (present) swapChain.Present(1, PresentFlags.None).CheckError();
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        context?.ClearState(); context?.Flush();
        target?.Dispose(); vertex?.Dispose();
        foreach (var pixel in pixels) pixel.Dispose();
        for (var i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
    }
}

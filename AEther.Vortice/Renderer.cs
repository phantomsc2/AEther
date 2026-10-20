using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace AEther.Vortice;

/// <summary>D3D11 fullscreen passes. Frame rendering may run on the dedicated render thread.</summary>
public sealed partial class Renderer : IDisposable
{
    readonly Lock graphicsGate = new();
    readonly List<IDisposable> resources = [];
    ID3D11Device device = null!;
    ID3D11DeviceContext context = null!;
    ColorOutput output = null!;
    ID3D11VertexShader vertex = null!;
    Dictionary<string, ShaderScene> scenes = new(StringComparer.Ordinal);
    readonly Dictionary<string, ShaderParameters> savedParameters = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11Buffer> namedBuffers = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11ShaderResourceView> namedTextures = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11SamplerState> namedSamplers = new(StringComparer.Ordinal);

    ID3D11PixelShader outputPixel = null!;

    MandelFluidSimulation? fluid;
    MandelbenchDistanceField? distanceField;
    MomentLightTransport? lightTransport;
    FourierLightTransport? fourierTransport;
    RasterLightTransport? rasterTransport;
    ImageFeedback? feedback;
    string? previousScene;
    internal MandelFluidSimulation Fluid => fluid!;
    internal MandelFluidSimulation.Programs FluidPrograms => scenes[SceneName].Fluid!;
    ID3D11Buffer frameBuffer = null!;
    readonly ID3D11Texture2D[] textures = new ID3D11Texture2D[5];
    readonly ID3D11ShaderResourceView[] views = new ID3D11ShaderResourceView[5];
    readonly string shaderDirectory;
    int width, height;
    double previousTime;
    double previousMusicalTime, movementTime;
    double shapeTime;
    double distortionMomentum;
    bool disposed;
    long previousFrame;
    public AudioHistory Audio { get; }
    public string SceneName { get; private set; } = "Mandelbox";
    public IReadOnlyCollection<ShaderScene> Scenes => scenes.Values;
    public ShaderScene? ActiveShader => scenes.GetValueOrDefault(SceneName);
    public event EventHandler? ShadersChanged;
    public void SelectScene(string name)
    {
        if (!scenes.ContainsKey(name)) throw new ArgumentException("Unknown scene.", nameof(name));
        SceneName = name;
    }
    public string? ShaderError { get; private set; }
    public string ShaderDirectory => shaderDirectory;
    public bool HighPrecisionOutput => output.HighPrecision;
    public string OutputDescription => output.Description;
    /// <summary>Wait before each live frame, including the first. Owned by the renderer.</summary>
    public WaitHandle FrameReady => output.FrameReady;
    public string AdapterDescription { get; } = string.Empty;
    internal float[] ReadPresentedPixels() { lock (graphicsGate) return output.ReadPresentedPixels(); }
    // Optional GPU timestamp markers used by the transport profiling tool.
    internal Action<string, bool>? TransportPassMarker { get; set; }
    internal double FourierAggregationMilliseconds => fourierTransport?.LastAggregationMilliseconds ?? 0;
    internal bool UnshadowedAirlightReady => rasterTransport?.LookupReady == true;

    T Own<T>(T value) where T : IDisposable { resources.Add(value); return value; }

    public Renderer(IntPtr window, int width, int height, int bands, double analysisRate,
        double historySeconds = 2, string? shaderDirectory = null, bool highPrecisionOutput = true,
        Action<IDXGISwapChain1?>? attachSwapChain = null, TimeProvider? timeProvider = null)
    {
        Audio = new(bands, analysisRate, historySeconds, timeProvider: timeProvider);
        this.shaderDirectory = shaderDirectory ?? FindShaderDirectory();
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory2>();
            CreateGraphicsDevice(factory);
            Own(device); Own(context);
            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
                AdapterDescription = adapter.Description.Description;
            Debug.WriteLine($"AEther GPU: {AdapterDescription}");
            output = Own(new ColorOutput(device, context, factory, window, width, height, highPrecisionOutput, attachSwapChain));
            if (attachSwapChain == null)
                factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter).CheckError();
            frameBuffer = Own(device.CreateBuffer((uint)Marshal.SizeOf<Frame>(), BindFlags.ConstantBuffer));

            namedBuffers.Add("FrameConstants", frameBuffer);

            for (var i = 0; i < textures.Length; i++)
            {
                textures[i] = Own(device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)(i == 4 ? 1 : bands), Height = (uint)(i < 2 ? 1 : Audio.Rows),
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
            namedSamplers.Add("Linear", sampler);
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

    void CreateGraphicsDevice(IDXGIFactory2 factory)
    {
        // DXGI supplies performance order: no assumption about adapter positions,
        // vendor IDs, memory sizes or whether the display is wired to the GPU.
        using var preferred = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        if (preferred != null)
        {
            for (uint i = 0; preferred.EnumAdapterByGpuPreference<IDXGIAdapter1>(i, GpuPreference.HighPerformance,
                     out var candidate).Success; i++)
            {
                using var adapter = candidate;
                if (adapter == null || (adapter.Description1.Flags & AdapterFlags.Software) != 0) continue;
                var result = D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0], out ID3D11Device candidateDevice, out ID3D11DeviceContext candidateContext);
                if (result.Success)
                {
                    device = candidateDevice; context = candidateContext;
                    return;
                }
                candidateContext?.Dispose(); candidateDevice?.Dispose();
            }
        }
        // Older Windows versions, or no usable adapter in the preferred list.
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out device, out context).CheckError();
    }

    static string FindShaderDirectory()
    {
        // Prefer editable source files when running from Rider or the repository.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "AEther.Vortice", "Shaders");
                if (File.Exists(Path.Combine(candidate, "Common", "Inputs.hlsl"))) return candidate;
            }
        return Path.Combine(AppContext.BaseDirectory, "Shaders");
    }

    public bool ReloadShaders() { lock (graphicsGate) return ReloadShadersCore(); }

    bool ReloadShadersCore()
    {
        ID3D11VertexShader? nextVertex = null;
        ID3D11PixelShader? nextOutput = null;
        var nextScenes = new Dictionary<string, ShaderScene>(StringComparer.Ordinal);
        try
        {
            var common = ShaderSource.Read(shaderDirectory, "Common/Inputs.hlsl");
            nextVertex = device.CreateVertexShader(Compiler.Compile(common, "VS", "Inputs.hlsl", "vs_5_0").Span);
            nextOutput = device.CreatePixelShader(Compiler.Compile(common + ShaderSource.Read(shaderDirectory, "Common/Present.hlsl"), "PS", "Present.hlsl", "ps_5_0").Span);
            foreach (var path in Directory.EnumerateFiles(Path.Combine(shaderDirectory, "Scenes"), "*.hlsl").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var scene = new ShaderScene(device, name, common + ShaderSource.Read(shaderDirectory, Path.Combine("Scenes", name + ".hlsl")), shaderDirectory);
                nextScenes.Add(name, scene);
                if (scene.Parameters != null)
                {
                    if (scenes.GetValueOrDefault(name)?.Parameters is { } old) scene.Parameters.Restore(old);
                    else if (savedParameters.TryGetValue(name, out old)) scene.Parameters.Restore(old);
                }
            }
            if (nextScenes.Count == 0) throw new InvalidOperationException("Shaders/Scenes must contain at least one .hlsl scene.");
            foreach (var scene in scenes.Values)
            {
                if (scene.Parameters != null) savedParameters[scene.Name] = scene.Parameters;
                scene.Dispose();
            }
            scenes = nextScenes;
            if (!scenes.ContainsKey(SceneName)) SceneName = scenes.Keys.First();
            vertex?.Dispose(); vertex = nextVertex;
            outputPixel?.Dispose(); outputPixel = nextOutput;
            ShaderError = null;
        }
        catch (Exception error)
        {
            nextVertex?.Dispose(); nextOutput?.Dispose();
            foreach (var scene in nextScenes.Values) scene.Dispose();
            ShaderError = error.Message;
            return false;
        }
        ShadersChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
    public void Resize(int newWidth, int newHeight)
    {
        lock (graphicsGate) ResizeCore(newWidth, newHeight);
    }

    void ResizeCore(int newWidth, int newHeight)
    {
        if (newWidth < 1 || newHeight < 1 || newWidth == width && newHeight == height) return;
        output.Resize(newWidth, newHeight);
        width = newWidth; height = newHeight;
        ReleaseGeometryTargets();
        // Resizing changes the physical domain. Recreate lazily on next fluid frame.
        fluid?.Dispose(); fluid = null;
        distanceField?.Dispose(); distanceField = null;
        feedback?.Dispose(); feedback = null;
    }

    public void Render(bool present = true, uint syncInterval = 1)
    {
        if (syncInterval > 4) throw new ArgumentOutOfRangeException(nameof(syncInterval));
        lock (graphicsGate) RenderCore(present, syncInterval);
    }

    unsafe void RenderCore(bool present, uint syncInterval)
    {
        if (output.SceneTarget == null) return;
        var frameStart = Stopwatch.GetTimestamp();
        if (previousFrame != 0) Audio.Profile?.Record("render.frame_interval_ms", Stopwatch.GetElapsedTime(previousFrame, frameStart).TotalMilliseconds);
        previousFrame = frameStart;
        var now = Audio.Now;
        var dt = Math.Max(0, now - previousTime);
        Audio.Advance(dt);
        var global = Audio.GlobalSTBR;
        static float Response(float value, float sensitivity) => value > 0 ? 1 / (1 + sensitivity / value) : 0;
        var bed = Response(global.Z, .01f);
        var structure = Response(global.X, .035f);
        var interaction = Response(global.W, .02f);
        var rise = Response(global.Y, .02f);
        // Material evolution follows sustained components. Onsets can pulse a
        // silhouette without permanently advancing its underlying shape.
        shapeTime += Math.Clamp(dt, 0, .05)
            * Math.Max(Audio.Components.X + .7 * Audio.Components.Z, .55 * structure + .65 * bed);
        // A passage builds deformation over sustained energy/noise, then coasts
        // through brief gaps rather than changing strength on every analysis row.
        var passageEnergy = 1 - Math.Exp(-2.5 * Math.Max(0, Audio.Energy));
        var distortionTarget = Math.Clamp(Math.Max(.55 * Audio.Components.Z + .45 * passageEnergy,
            .55 * bed + .25 * interaction + .2 * structure), 0, 1);
        var distortionResponse = 1 - Math.Exp(-Math.Clamp(dt, 0, .05)
            / (distortionTarget > distortionMomentum ? .85 : 3.2));
        distortionMomentum += (distortionTarget - distortionMomentum) * distortionResponse;
        var activity = Math.Clamp((Audio.Components.Z + .2f * Audio.Components.Y - .02f) / .53f, 0, 1);
        activity = Math.Clamp(Math.Max(activity, .8f * bed + .2f * structure + .25f * rise), 0, 1);
        var mobility = ActiveShader?.Fluid != null ? activity * activity * (3 - 2 * activity) : 1;
        movementTime += (Audio.MusicalTime - previousMusicalTime) * mobility;
        previousMusicalTime = Audio.MusicalTime;
        previousTime = now;
        // Upload after draining the mailbox; capture/analysis never touches a graphics lock.
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        for (var i = 0; i < textures.Length; i++)
        {
            var source = i == 4 ? Audio.GlobalHistory : i < 2 ? Audio.Spectrum[i] : Audio.History[i - 2];
            fixed (float* data = source)
                context.UpdateSubresource(textures[i], 0, null, (IntPtr)data, (uint)((i == 4 ? 1 : Audio.Bands) * 16), 0);
        }
        var frame = new Frame
        {
            A = new((float)now, (float)dt, Audio.Shift, width / (float)height),
            B = new(Audio.Flash, (float)shapeTime, (float)distortionMomentum, Audio.TimeScale),
            C = new(Audio.Components, Audio.FractalEnergy),
            D = new(Audio.Energy, Audio.Flash, Audio.Flash * .3f, Audio.Energy),
            E = new((float)Audio.MusicalTime, Audio.AvailableSpan, Audio.NewestAge,
                Audio.FractalDepth),
            F = new(width, height, (float)movementTime, (float)Audio.Seconds),
            GlobalSTBR = Audio.GlobalSTBR,
        };
        context.UpdateSubresource(frame, frameBuffer);
        context.RSSetViewport(0, 0, width, height);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);



        var active = ActiveShader!;
        if (active.Raster != null)
        {
            rasterTransport ??= new(device, context);
            namedSamplers["RasterShadowSampler"] = rasterTransport.ShadowSampler;
            namedBuffers["RasterLightConstants"] = rasterTransport.LightBuffer;
            namedBuffers["RasterAirlightConstants"] = rasterTransport.AirlightBuffer;
            namedSamplers["RasterLutSampler"] = rasterTransport.LookupSampler;
            rasterTransport.PassMarker = TransportPassMarker;
            rasterTransport.Prepare(active.Raster, active.Parameters!, Audio, (float)now, width, height,
                namedBuffers, namedTextures, namedSamplers);
            context.VSSetShader(vertex);
            context.RSSetViewport(0, 0, width, height);
            rasterTransport.IntegrateAirlight(active.Raster, active.Parameters!, namedBuffers);
        }
        if (active.FourierShadow != null)
        {
            fourierTransport ??= new(device, context);
            fourierTransport.PassMarker = TransportPassMarker;
            fourierTransport.Prepare(active.FourierShadow, active.Parameters!, (float)now,
                namedBuffers, namedBuffers, namedTextures);
            context.RSSetViewport(0, 0, width, height);
        }
        if (active.Atmosphere != null)
        {
            lightTransport ??= new(device, context);
            lightTransport.PassMarker = TransportPassMarker;
            lightTransport.PrecalculateAtmosphere(active.Atmosphere, active.Parameters!, namedBuffers);
            bool atmosphereOnly = ReadBoolean("View.ShowAtmosphereLookup");
            if (active.CameraTransport != null)
            {
                lightTransport.PrecalculateCamera(active.CameraTransport, active.Parameters!, namedBuffers, width, height, !atmosphereOnly, (float)now);
                lightTransport.BindCamera(namedTextures);
            }
            if (active.Lighting != null)
            {
                lightTransport.IntegrateLights(active.LightTransport!, active.Lighting, active.Parameters!, namedBuffers,
                    width, height, (float)now, atmosphereOnly);
                namedTextures["LightRadiance"] = lightTransport.Radiance;
            }
            lightTransport.BindPreview(namedTextures);
            namedTextures["AtmosphereOpticalDepth"] = lightTransport.Atmosphere;
            namedSamplers["AtmosphereSampler"] = lightTransport.Sampler;
            context.RSSetViewport(0, 0, width, height);
        }
        namedTextures["Spectrum0"] = views[0]; namedTextures["Spectrum1"] = views[1];
        namedTextures["Histogram0"] = views[2]; namedTextures["Histogram1"] = views[3];
        namedTextures["GlobalHistogram"] = views[4];
        // Compatibility names now refer to the core evidence; there is no scene filter pass.
        namedTextures["FilteredHistogram0"] = views[2]; namedTextures["FilteredHistogram1"] = views[3];
        if (active.GeometryVertex != null) DrawGeometry(active);
        if (active.Fluid != null)
        {
            int gridLimit = SceneName switch
            {
                "MandelFluidv4" => ReadBoolean("Fluid.LowQuality") ? 128 : 512,
                "MandelFluidv3" => 512,
                _ => 256,
            };
            bool squareGrid = SceneName == "MandelFluidv4";
            if (fluid != null && (fluid.GridLimit != gridLimit || fluid.SquareGrid != squareGrid))
            { fluid.Dispose(); fluid = null; }
            fluid ??= new(device, context, width, height, gridLimit, squareGrid);
            if (previousScene != SceneName || dt > .5) fluid.Clear();
            fluid.Advance(dt, Audio, active.Fluid,
                active.Parameters, namedBuffers, namedTextures, namedSamplers);
            namedTextures["FluidDye"] = fluid.Dye;
            if (fluid.Age != null) namedTextures["FluidAge"] = fluid.Age;
            namedSamplers["FluidSampler"] = fluid.Sampler;
            context.RSSetViewport(0, 0, width, height);
        }
        if (active.Program.Uses("PreviousImage"))
        {
            feedback ??= new(device, context, width, height);
            if (previousScene != SceneName || dt > .5) feedback.Clear();
            feedback.Advance(dt, active, namedBuffers, namedTextures, namedSamplers);
        }
        previousScene = SceneName;
        if (active.Distance != null)
        {
            distanceField ??= new(device, context, width, height);
            distanceField.Update(active.Distance, active.Parameters!, namedBuffers);
            namedTextures["DistanceField"] = distanceField.Field;
            namedSamplers["DistanceSampler"] = distanceField.Sampler;
            context.RSSetViewport(0, 0, width, height);
        }
        context.OMSetRenderTargets(output.SceneTarget);
        (active.Composite ?? active.Program).Bind(context, active.Parameters, namedBuffers, namedTextures, namedSamplers);
        if (active.FourierShadow != null) TransportPassMarker?.Invoke("Fourier camera", true);
        if (active.Raster != null) TransportPassMarker?.Invoke("Raster composite", true);
        context.Draw(3, 0);
        if (active.Raster != null) TransportPassMarker?.Invoke("Raster composite", false);
        if (active.FourierShadow != null) TransportPassMarker?.Invoke("Fourier camera", false);
        output.Draw(outputPixel, now);
        Audio.Profile?.Elapsed("render.draw_cpu_ms", frameStart);
        if (Audio.LatestCaptureTimestamp != 0)
            Audio.Profile?.Elapsed("render.capture_to_submission_ms", Audio.LatestCaptureTimestamp);
        if (Audio.LatestSourceTimestamp.HasValue)
            Audio.Profile?.Elapsed("render.source_to_submission_ms", Audio.LatestSourceTimestamp.Value);
        var presentStart = Stopwatch.GetTimestamp();
        if (present) output.Present(syncInterval);
        if (present) Audio.Profile?.Elapsed("render.present_wait_ms", presentStart);
    }

    /// <summary>Display-encoded RGBA8 export; dithering avoids reintroducing bands in PNGs.</summary>
    public byte[] ReadPixels(bool dither = true) { lock (graphicsGate) return output.ReadPixels(dither); }

    /// <summary>Match a composition swap chain's physical pixels to the host's XAML scale.</summary>
    public void SetCompositionScale(float x, float y) { lock (graphicsGate) output.SetCompositionScale(x, y); }

    [StructLayout(LayoutKind.Sequential)]
    struct Frame { public Vector4 A, B, C, D, E, F, GlobalSTBR; }


    public void Dispose()
    {
        lock (graphicsGate) DisposeCore();
    }

    void DisposeCore()
    {
        if (disposed) return;
        disposed = true;
        context?.ClearState(); context?.Flush();
        vertex?.Dispose(); outputPixel?.Dispose();

        fluid?.Dispose();
        distanceField?.Dispose();
        lightTransport?.Dispose();
        fourierTransport?.Dispose();
        rasterTransport?.Dispose();
        feedback?.Dispose();
        ReleaseGeometryTargets(); geometryDepthState?.Dispose();
        geometryNoDepthState?.Dispose(); geometryAdditiveState?.Dispose();
        foreach (var scene in scenes.Values) scene.Dispose();
        for (var i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
    }
}

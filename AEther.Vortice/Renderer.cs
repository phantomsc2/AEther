using System.Numerics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;
using Serilog;

namespace AEther.Vortice;

/// <summary>D3D11 fullscreen passes. Frame rendering may run on the dedicated render thread.</summary>
public sealed partial class Renderer : IDisposable
{
    public ILogger Logger { get; private set; }
    bool initializing = true;
    Exception? shaderException;
    string? sizeError;
    readonly Lock graphicsGate = new();
    readonly List<IDisposable> resources = [];
    ID3D11Device device = null!;
    ID3D11DeviceContext context = null!;
    ColorOutput output = null!;
    GpuProfiler gpu = null!;
    ID3D11VertexShader vertex = null!;
    Dictionary<string, ShaderScene> scenes = new(StringComparer.Ordinal);
    readonly Dictionary<string, IReadOnlyDictionary<string, EffectSettings>> savedSettings = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11Buffer> namedBuffers = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11ShaderResourceView> namedTextures = new(StringComparer.Ordinal);
    readonly Dictionary<string, ID3D11SamplerState> namedSamplers = new(StringComparer.Ordinal);

    ID3D11PixelShader outputPixel = null!;

    readonly IReadOnlyList<SceneDefinition> sceneDefinitions;
    ID3D11Buffer frameBuffer = null!;
    readonly ID3D11Texture2D[] textures = new ID3D11Texture2D[4];
    readonly ID3D11ShaderResourceView[] views = new ID3D11ShaderResourceView[4];
    readonly string shaderDirectory;
    int width, height;
    double previousTime;
    double previousMusicalTime, movementTime;
    double shapeTime;
    double distortionMomentum;
    bool disposed;
    long previousFrame;
    CompositionClock? displayClock;
    public long? LastPresentationTimestamp { get; private set; }
    internal bool RawPresentationClock { get; set; }
    // Reference cadence for repeatable before/after developer profiling only.
    internal bool FixedFluidMaterialCadence { get; set; }
    public AudioHistory Audio { get; private set; }
    public AnalysisOutput? Input { get; private set; }
    public GraphicsOptions Options { get; private set; }
    public string? SceneName { get; private set; }
    public IReadOnlyCollection<ShaderScene> Scenes { get { lock (graphicsGate) return scenes.Values.ToArray(); } }
    public ShaderScene? ActiveShader { get { lock (graphicsGate) return SceneName == null ? null : scenes.GetValueOrDefault(SceneName); } }
    public event EventHandler? ShadersChanged;
    public IReadOnlyDictionary<string, EffectSettings>? GetSceneSettings(string name)
    {
        lock (graphicsGate) return scenes.GetValueOrDefault(name) is { ProgramReady: true } scene
            ? scene.CaptureSettings() : savedSettings.GetValueOrDefault(name);
    }

    public void RestoreSceneSettings(string name, IReadOnlyDictionary<string, EffectSettings> settings)
    {
        lock (graphicsGate)
        {
            if (!scenes.TryGetValue(name, out var scene)) return;
            savedSettings[name] = settings;
            scene.RestoreSettings(settings);
        }
    }

    public void SelectScene(string? name)
    {
        lock (graphicsGate)
        {
            if (name == null) { ActiveShader?.Deactivate(); SceneName = null; return; }
            if (!scenes.TryGetValue(name, out var scene)) throw new ArgumentException("Unknown scene.", nameof(name));
            bool firstCompilation = !scene.ProgramReady;
            scene.EnsureCompiled();
            if (firstCompilation && savedSettings.TryGetValue(name, out var saved)) scene.RestoreSettings(saved);
            if (!scene.ProgramReady) throw new InvalidOperationException($"Scene '{name}' did not compile.");
            if (SceneName != name) ActiveShader?.Deactivate();
            SceneName = name;
        }
    }

    public string? ShaderError { get; private set; }
    public string ShaderDirectory => shaderDirectory;
    public bool HighPrecisionOutput => output.HighPrecision;
    public string OutputDescription => output.Description;
    /// <summary>Swap-chain readiness gate. Wait before sampling frame inputs even with compositor pacing. Owned by the renderer.</summary>
    public WaitHandle FrameReady => output.FrameReady;
    public string AdapterDescription { get; } = string.Empty;
    public string FluidWork => ActiveShader?.Effects.OfType<FluidEffect>().FirstOrDefault()?.Simulation is not { } fluid ? "inactive"
        : $"{fluid.Width}x{fluid.Height}, {fluid.LastSteps} velocity / {fluid.LastMaterialSteps} material steps, {fluid.LastDraws} draws";
    public long SkippedGpuTimingFrames => gpu.SkippedFrames;
    public long InvalidGpuTimingFrames => gpu.InvalidFrames;
    internal int LastGpuTimingQueryCount => gpu.LastQueryCount;
    internal void PollGpuTimings() => gpu.Poll();
    internal void GpuMarker(string name, bool begin)
    {
        gpu.Marker(name, begin);
        TransportPassMarker?.Invoke(name, begin);
    }
    internal float[] ReadPresentedPixels() { lock (graphicsGate) return output.ReadPresentedPixels(); }
    // Optional GPU timestamp markers used by the transport profiling tool.
    internal Action<string, bool>? TransportPassMarker { get; set; }

    T Own<T>(T value) where T : IDisposable { resources.Add(value); return value; }

    public Renderer(IntPtr window, int width, int height, AnalysisOutput input,
        string? shaderDirectory = null, Action<IDXGISwapChain1?>? attachSwapChain = null,
        string? initialScene = null, PipelineProfile? profile = null, GraphicsOptions? graphicsOptions = null, ILogger? logger = null, IReadOnlyList<SceneDefinition>? sceneDefinitions = null)
        : this(window, width, height, input.Domain.Length, input.AnalysisRate, graphicsOptions?.HistorySeconds ?? 2,
            shaderDirectory, attachSwapChain: attachSwapChain, initialScene: initialScene, profile: profile, logger: logger, sceneDefinitions: sceneDefinitions)
    {
        Input = input;
        if (graphicsOptions != null) SetGraphicsOptions(graphicsOptions);
    }

    public Renderer(IntPtr window, int width, int height, int bands, double analysisRate,
        double historySeconds = 2, string? shaderDirectory = null, bool highPrecisionOutput = true,
        Action<IDXGISwapChain1?>? attachSwapChain = null, TimeProvider? timeProvider = null,
        string? initialScene = null, PipelineProfile? profile = null, ILogger? logger = null, IReadOnlyList<SceneDefinition>? sceneDefinitions = null)
    {
        this.sceneDefinitions = sceneDefinitions ?? SceneDefinition.Defaults;
        Logger = (logger ?? Log.Logger).ForContext<Renderer>();
        using var startup = profile?.Measure("startup.gpu_ms");
        Options = new(historySeconds);
        Options.Validate();
        Audio = new(bands, analysisRate, historySeconds, timeProvider: timeProvider);
        Audio.Profile = profile;
        if (initialScene != null) SceneName = initialScene;
        this.shaderDirectory = shaderDirectory ?? FindShaderDirectory();
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory2>();
            using (profile?.Measure("startup.gpu_device_ms"))
                CreateGraphicsDevice(factory);
            Own(device); Own(context);
            gpu = Own(new GpuProfiler(device, context));
            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
                AdapterDescription = adapter.Description.Description;
            Logger.Information("Graphics adapter is {Adapter}", AdapterDescription);
            using (profile?.Measure("startup.gpu_output_ms"))
                output = Own(new ColorOutput(device, context, factory, window, width, height, highPrecisionOutput, attachSwapChain, logger: Logger));
            Logger.Information("Graphics output is {Output}; dimensions {Width} x {Height}", output.Description, width, height);
            if (attachSwapChain == null)
                factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter).CheckError();
            frameBuffer = Own(device.CreateBuffer((uint)Marshal.SizeOf<Frame>(), BindFlags.ConstantBuffer));

            namedBuffers.Add("FrameConstants", frameBuffer);

            ReplaceAudioTextures(Audio);
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
            using (profile?.Measure("startup.gpu_shaders_ms"))
                if (!ReloadShaders()) throw new InvalidOperationException(ShaderError, shaderException);
            initializing = false;
            Logger.Debug("Renderer initialized; scene {Scene}; shader directory {ShaderDirectory}; analysis {AnalysisRate} Hz; bands {Bands}; history {HistorySeconds} s", SceneName, this.shaderDirectory, analysisRate, bands, historySeconds);
        }
        catch { Dispose(); throw; }
    }

    public void PostAudio(SampleEvent<double> input, int channelCount) => Audio.Post(input, channelCount);

    void CreateGraphicsDevice(IDXGIFactory2 factory)
    {
        // Arc is the common development/performance target for every scene and tool.
        // Never silently hide an Arc initialization failure by using a faster GPU.
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                var description = adapter.Description1;
                if (!IsArcAdapter(description.VendorId, description.Description,
                    (description.Flags & AdapterFlags.Software) != 0)) continue;
                var result = D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    [FeatureLevel.Level_11_0], out ID3D11Device candidateDevice, out ID3D11DeviceContext candidateContext);
                if (result.Failure)
                {
                    candidateContext?.Dispose(); candidateDevice?.Dispose();
                    throw new InvalidOperationException($"Cannot initialize Intel Arc ({description.Description}): {result}");
                }
                device = candidateDevice; context = candidateContext;
                return;
            }
        }
        Logger.Information("Preferred Intel Arc adapter absent; activating default hardware adapter fallback");
        // Portable fallback only when no Arc hardware is present.
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out device, out context).CheckError();
    }

    internal static bool IsArcAdapter(uint vendor, string description, bool software) =>
        !software && vendor == 0x8086 && description.Contains("Arc", StringComparison.OrdinalIgnoreCase);

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

    public bool CompileScene(string name) => CompileScene(name, reportFailure: true);

    bool CompileScene(string name, bool reportFailure)
    {
        lock (graphicsGate)
        {
            if (!scenes.TryGetValue(name, out var scene)) return false;
            try
            {
                if (!scene.ProgramReady)
                {
                    scene.EnsureCompiled();
                    if (savedSettings.TryGetValue(name, out var saved)) scene.RestoreSettings(saved);
                }
                else
                {
                    var replacement = scene.Recompile();
                    scene.ReplaceWith(replacement);
                    replacement.Dispose();
                }
                ShaderError = null;
                ShadersChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception error)
            {
                ShaderError = error.Message;
                shaderException = error;
                if (reportFailure) ShaderFailed(error);
                return false;
            }
        }
    }

    public bool CompileActiveScene() => SceneName is { } name && CompileScene(name);

    public bool ReloadScene(string name)
    {
        lock (graphicsGate)
        {
            if (!scenes.TryGetValue(name, out var scene)) return false;
            try
            {
                return ReloadSceneCore(name, scene);
            }
            catch (Exception error)
            {
                ShaderError = error.Message;
                ShaderFailed(error);
                return false;
            }
        }
    }

    public bool ReloadActiveScene()
    {
        lock (graphicsGate)
        {
            var name = SceneName;
            if (name == null || !scenes.TryGetValue(name, out var scene)) return false;
            try { return ReloadSceneCore(name, scene); }
            catch (Exception error) { ShaderError = error.Message; ShaderFailed(error); return false; }
        }
    }

    bool ReloadSceneCore(string name, ShaderScene scene)
    {
        var common = ShaderSource.IncludeFile(shaderDirectory, "Common/Inputs.hlsl");
        var source = common + ShaderSource.IncludeFile(shaderDirectory, Path.Combine("Scenes", name + ".hlsl"));
        var replacement = new ShaderScene(device, context, sceneDefinitions.Single(d => d.Name == name), source);
        if (StringComparer.Ordinal.Equals(SceneName, name))
        {
            replacement.EnsureCompiled();
            replacement.RestoreSettings(scene.CaptureSettings());
        }
        else if (scene.ProgramReady) savedSettings[name] = scene.CaptureSettings();
        scenes[name] = replacement;
        scene.Dispose();
        ShaderError = null;
        Logger.Information("Scene {Scene} reloaded", name);
        ShadersChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    bool ReloadShadersCore()
    {
        ID3D11VertexShader? nextVertex = null;
        ID3D11PixelShader? nextOutput = null;
        var nextScenes = new Dictionary<string, ShaderScene>(StringComparer.Ordinal);
        try
        {
            var common = ShaderSource.IncludeFile(shaderDirectory, "Common/Inputs.hlsl");
            nextVertex = device.CreateVertexShader(ShaderSource.Compile(common, "VS", "Inputs.hlsl", "vs_5_0").Span);
            nextOutput = device.CreatePixelShader(ShaderSource.Compile(common + ShaderSource.IncludeFile(shaderDirectory, "Common/Present.hlsl"), "PS", "Present.hlsl", "ps_5_0").Span);
            foreach (var definition in sceneDefinitions.Where(d => File.Exists(Path.Combine(shaderDirectory, "Scenes", d.Name + ".hlsl"))))
            {
                var name = definition.Name;
                var source = common + ShaderSource.IncludeFile(shaderDirectory, Path.Combine("Scenes", name + ".hlsl"));
                if (name == SceneName)
                {
                    var scene = new ShaderScene(device, context, sceneDefinitions.Single(d => d.Name == name), source);
                    scene.EnsureCompiled();
                    if (GetSceneSettings(name) is { } old) scene.RestoreSettings(old);
                    nextScenes.Add(name, scene);
                }
                else nextScenes.Add(name, new ShaderScene(device, context, sceneDefinitions.Single(d => d.Name == name), source));
            }
            var selected = SceneName == null ? null : nextScenes.GetValueOrDefault(SceneName);
            foreach (var scene in scenes.Values)
            {
                if (scene.ProgramReady) savedSettings[scene.Name] = scene.CaptureSettings();
                scene.Dispose();
            }
            scenes = nextScenes;
            SceneName = selected?.Name;
            vertex?.Dispose(); vertex = nextVertex; nextVertex = null;
            outputPixel?.Dispose(); outputPixel = nextOutput; nextOutput = null;
            ShaderError = null;
        }
        catch (Exception error)
        {
            nextVertex?.Dispose(); nextOutput?.Dispose();
            foreach (var scene in nextScenes.Values) scene.Dispose();
            ShaderError = error.Message;
            ShaderFailed(error);
            return false;
        }
        ShadersChanged?.Invoke(this, EventArgs.Empty);
        if (!initializing) Logger.Information("Shaders reloaded; active scene {Scene}", SceneName);
        return true;
    }

    void ShaderFailed(Exception error)
    {
        shaderException = error;
        if (!initializing) Logger.Error(error, "Shader operation failed; active scene remains {Scene}", SceneName);
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
        // Scene resources are replaced transactionally on the next frame.
    }

    public void Render(bool present = true, uint syncInterval = 1, long? presentationTimestamp = null,
        bool renderCanvas = true, bool renderDebug = false)
    {
        if (syncInterval > 4) throw new ArgumentOutOfRangeException(nameof(syncInterval));
        lock (graphicsGate) RenderCore(present, syncInterval, presentationTimestamp, renderCanvas, renderDebug);
    }

    unsafe void RenderCore(bool present, uint syncInterval, long? presentationTimestamp, bool renderCanvas, bool renderDebug)
    {
        renderCanvas &= ActiveShader != null;
        PollCanvasProgress();
        if (output.SceneTarget == null || (!renderCanvas && !renderDebug)) return;
        var frameStart = Stopwatch.GetTimestamp();
        using var gpuFrame = gpu.BeginFrame(Audio.Profile, frameStart);
        if (previousFrame != 0) Audio.Profile?.Record("render.frame_interval_ms", Stopwatch.GetElapsedTime(previousFrame, frameStart).TotalMilliseconds, frameStart);
        previousFrame = frameStart;
        if (present && !presentationTimestamp.HasValue)
            presentationTimestamp = (displayClock ??= Own(new CompositionClock())).NextFrameTimestamp(Audio.Profile, !RawPresentationClock);
        // Rasterize for the compositor's next display time rather than this thread's
        // jittery wake-up time. Offline renders retain their injected host clock.
        if (presentationTimestamp.HasValue && LastPresentationTimestamp.HasValue)
            presentationTimestamp = Math.Max(presentationTimestamp.Value, LastPresentationTimestamp.Value);
        if (presentationTimestamp.HasValue)
        {
            if (LastPresentationTimestamp.HasValue)
                Audio.Profile?.Record("render.display_interval_ms", AudioClock.Seconds(
                    presentationTimestamp.Value - LastPresentationTimestamp.Value) * 1000);
            Audio.Profile?.Record("render.display_lead_ms", AudioClock.Seconds(presentationTimestamp.Value - frameStart) * 1000);
        }
        LastPresentationTimestamp = presentationTimestamp;
        var now = Math.Max(previousTime, presentationTimestamp.HasValue ? Audio.TimeAt(presentationTimestamp.Value) : Audio.Now);
        var dt = Math.Max(0, now - previousTime);
        Audio.Advance(dt, presentationTimestamp);
        // Material evolution follows sustained components. Onsets can pulse a
        // silhouette without permanently advancing its underlying shape.
        shapeTime += Math.Clamp(dt, 0, .05)
            * (Audio.Components.X + .7 * Audio.Components.Z);
        // A passage builds deformation over sustained energy/noise, then coasts
        // through brief gaps rather than changing strength on every analysis row.
        var passageEnergy = 1 - Math.Exp(-2.5 * Math.Max(0, Audio.Energy));
        var distortionTarget = Math.Clamp(.55 * Audio.Components.Z + .45 * passageEnergy, 0, 1);
        var distortionResponse = 1 - Math.Exp(-Math.Clamp(dt, 0, .05)
            / (distortionTarget > distortionMomentum ? .85 : 3.2));
        distortionMomentum += (distortionTarget - distortionMomentum) * distortionResponse;
        movementTime += Audio.MusicalTime - previousMusicalTime;
        previousMusicalTime = Audio.MusicalTime;
        previousTime = now;
        GpuMarker("Audio upload", true);
        UploadAudio();
        GpuMarker("Audio upload", false);
        var frame = new Frame
        {
            A = new((float)now, (float)dt, Audio.Shift, width / (float)height),
            B = new(Audio.Flash, (float)shapeTime, (float)distortionMomentum, Audio.TimeScale),
            C = new(Audio.Components, Audio.FractalEnergy),
            D = new(Audio.Energy, Audio.Flash, Audio.Flash * .3f, Audio.Energy),
            E = new((float)Audio.MusicalTime, Audio.AvailableSpan, Audio.NewestAge,
                Audio.FractalDepth),
            F = new(width, height, (float)movementTime, (float)Audio.Seconds),
        };
        context.UpdateSubresource(frame, frameBuffer);
        context.RSSetViewport(0, 0, width, height);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(vertex);



        if (renderCanvas)
        {
            DrawCanvas(now, dt);
            if (present) SubmitCanvasProgress();
        }
        bool debugDrawn = false;
        if (renderDebug)
        {
            GpuMarker("Debug", true);
            debugDrawn = DrawDebug(frame, now, present, renderCanvas);
            GpuMarker("Debug", false);
        }
        gpu.EndFrame();
        Audio.Profile?.Record("render.draw_cpu_ms", Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds, frameStart);
        if (Audio.LatestCaptureTimestamp != 0)
            Audio.Profile?.Elapsed("render.capture_to_submission_ms", Audio.LatestCaptureTimestamp);
        if (Audio.LatestSourceTimestamp.HasValue)
            Audio.Profile?.Elapsed("render.source_to_submission_ms", Audio.LatestSourceTimestamp.Value);
        var presentStart = Stopwatch.GetTimestamp();
        if (present && debugDrawn) PresentDebug(renderCanvas, syncInterval);
        if (present && renderCanvas) output.Present(syncInterval);
        if (present) Audio.Profile?.Elapsed("render.present_wait_ms", presentStart);
    }

    void DrawCanvas(double now, double dt)
    {
        var active = ActiveShader!;
        if (!active.ProgramReady && !CompileScene(active.Name, reportFailure: false))
            throw new InvalidOperationException(ShaderError, shaderException);
        active = ActiveShader!;
        namedTextures["Spectrum0"] = views[0]; namedTextures["Spectrum1"] = views[1];
        namedTextures["Histogram0"] = views[2]; namedTextures["Histogram1"] = views[3];
        // Compatibility names refer to the core evidence, including in geometry stages.
        namedTextures["FilteredHistogram0"] = views[2]; namedTextures["FilteredHistogram1"] = views[3];
        active.Draw(new EffectFrame(width, height, dt, Audio.TimeScale / Audio.Seconds,
            vertex, output.SceneTarget!, namedBuffers, namedTextures, namedSamplers,
            GpuMarker, gpu.Annotate, ReportSizeError, !FixedFluidMaterialCadence));
        GpuMarker("Output", true);
        output.Draw(outputPixel, now, Options.Exposure);
        GpuMarker("Output", false);
    }

    void ReportSizeError(string? error)
    {
        if (error == null && ShaderError != sizeError) return;
        sizeError = error;
        if (ShaderError == error) return;
        ShaderError = error;
        ShadersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Display-encoded RGBA8 export; dithering avoids reintroducing bands in PNGs.</summary>
    public byte[] ReadPixels(bool dither = true) { lock (graphicsGate) return output.ReadPixels(vertex, outputPixel, dither); }

    /// <summary>Match a composition swap chain's physical pixels to the host's XAML scale.</summary>
    public void SetCompositionScale(float x, float y) { lock (graphicsGate) output.SetCompositionScale(x, y); }

    [StructLayout(LayoutKind.Sequential)]
    struct Frame { public Vector4 A, B, C, D, E, F; }


    public void Dispose()
    {
        lock (graphicsGate) DisposeCore();
    }

    void DisposeCore()
    {
        if (disposed) return;
        disposed = true;
        Logger.Debug("Disposing renderer and graphics resources");
        context?.ClearState(); context?.Flush();
        DetachDebugOutput();
        vertex?.Dispose(); outputPixel?.Dispose();

        foreach (var scene in scenes.Values) scene.Dispose();
        for (var i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
    }
}

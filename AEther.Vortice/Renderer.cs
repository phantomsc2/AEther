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
    GpuProfiler gpu = null!;
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
    public AudioHistory Audio { get; }
    public string SceneName { get; private set; } = "Mandelbox";
    public IReadOnlyCollection<ShaderScene> Scenes { get { lock (graphicsGate) return scenes.Values.ToArray(); } }
    public ShaderScene? ActiveShader { get { lock (graphicsGate) return scenes.GetValueOrDefault(SceneName); } }
    public event EventHandler? ShadersChanged;
    public void SelectScene(string name)
    {
        lock (graphicsGate)
        {
            if (!scenes.TryGetValue(name, out var scene)) throw new ArgumentException("Unknown scene.", nameof(name));
            scene.EnsureCompiled();
            if (!scene.ProgramReady) throw new InvalidOperationException($"Scene '{name}' did not compile.");
            SceneName = name;
        }
    }
    public string? ShaderError { get; private set; }
    public string ShaderDirectory => shaderDirectory;
    public bool HighPrecisionOutput => output.HighPrecision;
    public string OutputDescription => output.Description;
    /// <summary>DXGI pacing handle for hosts without compositor-clock pacing. Owned by the renderer.</summary>
    public WaitHandle FrameReady => output.FrameReady;
    public string AdapterDescription { get; } = string.Empty;
    public bool PreferDisplayAdapter { get; }
    readonly IntPtr displayMonitor;
    public bool IsDisplayMonitorCurrent(IntPtr window) => !PreferDisplayAdapter || MonitorFromWindow(window, 2) == displayMonitor;
    public string FluidWork => fluid == null || ActiveShader?.Fluid == null ? "inactive" : $"{fluid.Width}x{fluid.Height}, {fluid.LastSteps} steps, {fluid.LastDraws} draws";
    public long SkippedGpuTimingFrames => gpu.SkippedFrames;
    public long InvalidGpuTimingFrames => gpu.InvalidFrames;
    internal void PollGpuTimings() => gpu.Poll();
    internal void GpuMarker(string name, bool begin)
    {
        gpu.Marker(name, begin);
        TransportPassMarker?.Invoke(name, begin);
    }
    internal float[] ReadPresentedPixels() { lock (graphicsGate) return output.ReadPresentedPixels(); }
    // Optional GPU timestamp markers used by the transport profiling tool.
    internal Action<string, bool>? TransportPassMarker { get; set; }
    internal double FourierAggregationMilliseconds => fourierTransport?.LastAggregationMilliseconds ?? 0;
    internal bool UnshadowedAirlightReady => rasterTransport?.LookupReady == true;

    T Own<T>(T value) where T : IDisposable { resources.Add(value); return value; }

    public Renderer(IntPtr window, int width, int height, int bands, double analysisRate,
        double historySeconds = 2, string? shaderDirectory = null, bool highPrecisionOutput = true,
        Action<IDXGISwapChain1?>? attachSwapChain = null, TimeProvider? timeProvider = null,
        string? initialScene = null, bool preferDisplayAdapter = false, AudioHistory? audioHistory = null)
    {
        Audio = audioHistory ?? new(bands, analysisRate, historySeconds, timeProvider: timeProvider);
        if (initialScene != null) SceneName = initialScene;
        PreferDisplayAdapter = preferDisplayAdapter;
        displayMonitor = preferDisplayAdapter ? MonitorFromWindow(window, 2) : IntPtr.Zero;
        // New textures need the complete retained ring, even if the old GPU
        // already acknowledged every dirty row before the adapter change.
        if (audioHistory != null) Audio.InvalidateHistory();
        this.shaderDirectory = shaderDirectory ?? FindShaderDirectory();
        try
        {
            using var factory = CreateDXGIFactory1<IDXGIFactory2>();
            CreateGraphicsDevice(factory, window);
            Own(device); Own(context);
            gpu = Own(new GpuProfiler(device, context));
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
                    Width = (uint)bands, Height = (uint)(i < 2 ? 1 : Audio.Rows),
                    MipLevels = 1, ArraySize = 1, Format = Format.R32G32B32A32_Float,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                }));
                textures[i].DebugName = i < 2 ? $"Spectrum{i}" : $"Histogram{i - 2}";
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

    void CreateGraphicsDevice(IDXGIFactory2 factory, IntPtr window)
    {
        bool TryCreate(IDXGIAdapter1 adapter)
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0) return false;
            var result = D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out ID3D11Device candidateDevice, out ID3D11DeviceContext candidateContext);
            if (result.Success) { device = candidateDevice; context = candidateContext; return true; }
            candidateContext?.Dispose(); candidateDevice?.Dispose();
            return false;
        }
        if (PreferDisplayAdapter)
        {
            // Small scenes cost less than copying a native FP16 frame between GPUs.
            // Match the actual monitor rather than assuming an integrated adapter index.
            var monitor = displayMonitor;
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            using (adapter)
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                using (output)
                    if (output.Description.Monitor == monitor && TryCreate(adapter)) return;
        }
        // DXGI supplies performance order: no assumption about adapter positions,
        // vendor IDs, memory sizes or whether the display is wired to the GPU.
        using var preferred = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        if (preferred != null)
        {
            for (uint i = 0; preferred.EnumAdapterByGpuPreference<IDXGIAdapter1>(i, GpuPreference.HighPerformance,
                     out var candidate).Success; i++)
            {
                using var adapter = candidate;
                if (adapter != null && TryCreate(adapter)) return;
            }
        }
        // Older Windows versions, or no usable adapter in the preferred list.
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out device, out context).CheckError();
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

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

    public bool CompileScene(string name)
    {
        lock (graphicsGate)
        {
            if (!scenes.TryGetValue(name, out var scene)) return false;
            try
            {
                if (!scene.ProgramReady) scene.EnsureCompiled();
                else
                {
                    var replacement = scene.Recompile();
                    scene.ReplaceWith(replacement);
                    replacement.Dispose();
                }
                if (scene.Parameters != null && savedParameters.TryGetValue(name, out var old)) scene.Parameters.Restore(old);
                if (StringComparer.Ordinal.Equals(SceneName, name))
                {
                    fluid?.Dispose(); fluid = null;
                    distanceField?.Dispose(); distanceField = null;
                    feedback?.Dispose(); feedback = null;
                }
                ShaderError = null;
                ShadersChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception error)
            {
                ShaderError = error.Message;
                return false;
            }
        }
    }

    public bool CompileActiveScene() => CompileScene(SceneName);

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
                return false;
            }
        }
    }

    public bool ReloadActiveScene()
    {
        lock (graphicsGate)
        {
            var name = SceneName;
            if (!scenes.TryGetValue(name, out var scene)) return false;
            try { return ReloadSceneCore(name, scene); }
            catch (Exception error) { ShaderError = error.Message; return false; }
        }
    }

    bool ReloadSceneCore(string name, ShaderScene scene)
    {
        var common = ShaderSource.Read(shaderDirectory, "Common/Inputs.hlsl");
        var source = common + ShaderSource.Read(shaderDirectory, Path.Combine("Scenes", name + ".hlsl"));
        var replacement = new ShaderScene(device, name, source, shaderDirectory, compileImmediately: false);
        if (StringComparer.Ordinal.Equals(SceneName, name))
        {
            replacement.EnsureCompiled();
            if (scene.Parameters is { } old && replacement.Parameters is { } parameters) parameters.Restore(old);
        }
        else if (scene.Parameters is { } old && savedParameters.ContainsKey(name)) savedParameters[name] = old;
        scenes[name] = replacement;
        scene.Dispose();
        if (StringComparer.Ordinal.Equals(SceneName, name))
        {
            fluid?.Dispose(); fluid = null;
            distanceField?.Dispose(); distanceField = null;
            feedback?.Dispose(); feedback = null;
        }
        ShaderError = null;
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
            var common = ShaderSource.Read(shaderDirectory, "Common/Inputs.hlsl");
            nextVertex = device.CreateVertexShader(Compiler.Compile(common, "VS", "Inputs.hlsl", "vs_5_0").Span);
            nextOutput = device.CreatePixelShader(Compiler.Compile(common + ShaderSource.Read(shaderDirectory, "Common/Present.hlsl"), "PS", "Present.hlsl", "ps_5_0").Span);
            foreach (var path in Directory.EnumerateFiles(Path.Combine(shaderDirectory, "Scenes"), "*.hlsl").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var source = common + ShaderSource.Read(shaderDirectory, Path.Combine("Scenes", name + ".hlsl"));
                if (name == SceneName)
                {
                    var scene = new ShaderScene(device, name, source, shaderDirectory, compileImmediately: false);
                    scene.EnsureCompiled();
                    if (scenes.GetValueOrDefault(name)?.Parameters is { } old && scene.Parameters is { } parameters)
                        parameters.Restore(old);
                    nextScenes.Add(name, scene);
                }
                else nextScenes.Add(name, new ShaderScene(device, name, source, shaderDirectory, compileImmediately: false));
            }
            if (nextScenes.Count == 0) throw new InvalidOperationException("Shaders/Scenes must contain at least one .hlsl scene.");
            var selected = nextScenes.GetValueOrDefault(SceneName) ?? nextScenes.Values.First();
            if (!selected.ProgramReady) selected.EnsureCompiled();
            if (selected.Parameters != null && savedParameters.TryGetValue(selected.Name, out var saved)) selected.Parameters.Restore(saved);
            foreach (var scene in scenes.Values)
            {
                if (scene.Parameters != null) savedParameters[scene.Name] = scene.Parameters;
                scene.Dispose();
            }
            scenes = nextScenes;
            SceneName = selected.Name;
            vertex?.Dispose(); vertex = nextVertex; nextVertex = null;
            outputPixel?.Dispose(); outputPixel = nextOutput; nextOutput = null;
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
        using var gpuFrame = gpu.BeginFrame(Audio.Profile);
        var frameStart = Stopwatch.GetTimestamp();
        if (previousFrame != 0) Audio.Profile?.Record("render.frame_interval_ms", Stopwatch.GetElapsedTime(previousFrame, frameStart).TotalMilliseconds);
        previousFrame = frameStart;
        var now = Audio.Now;
        var dt = Math.Max(0, now - previousTime);
        Audio.Advance(dt);
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
        var activity = Math.Clamp((Audio.Components.Z + .2f * Audio.Components.Y - .02f) / .53f, 0, 1);
        var mobility = ActiveShader?.Fluid != null ? activity * activity * (3 - 2 * activity) : 1;
        movementTime += (Audio.MusicalTime - previousMusicalTime) * mobility;
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



        var active = ActiveShader!;
        if (!active.ProgramReady && !CompileScene(active.Name)) throw new InvalidOperationException(ShaderError);
        active = ActiveShader!;
        if (active.Raster != null)
        {
            rasterTransport ??= new(device, context);
            namedSamplers["RasterShadowSampler"] = rasterTransport.ShadowSampler;
            namedBuffers["RasterLightConstants"] = rasterTransport.LightBuffer;
            namedBuffers["RasterAirlightConstants"] = rasterTransport.AirlightBuffer;
            namedSamplers["RasterLutSampler"] = rasterTransport.LookupSampler;
            rasterTransport.PassMarker = GpuMarker;
            rasterTransport.Prepare(active.Raster, active.Parameters!, Audio, (float)now, width, height,
                namedBuffers, namedTextures, namedSamplers);
            context.VSSetShader(vertex);
            context.RSSetViewport(0, 0, width, height);
            rasterTransport.IntegrateAirlight(active.Raster, active.Parameters!, namedBuffers);
        }
        if (active.FourierShadow != null)
        {
            fourierTransport ??= new(device, context);
            fourierTransport.PassMarker = GpuMarker;
            fourierTransport.Prepare(active.FourierShadow, active.Parameters!, (float)now,
                namedBuffers, namedBuffers, namedTextures);
            context.RSSetViewport(0, 0, width, height);
        }
        if (active.Atmosphere != null)
        {
            lightTransport ??= new(device, context);
            lightTransport.PassMarker = GpuMarker;
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
        // Compatibility names now refer to the core evidence; there is no scene filter pass.
        namedTextures["FilteredHistogram0"] = views[2]; namedTextures["FilteredHistogram1"] = views[3];
        if (active.GeometryVertex != null)
        {
            GpuMarker("Geometry", true);
            DrawGeometry(active);
            GpuMarker("Geometry", false);
        }
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
            fluid.PassMarker = GpuMarker;
            fluid.DrawMarker = gpu.Annotate;
            if (previousScene != SceneName || dt > .5) fluid.Clear();
            GpuMarker("Fluid", true);
            fluid.Advance(dt, Audio, active.Fluid,
                active.Parameters, namedBuffers, namedTextures, namedSamplers);
            GpuMarker("Fluid", false);
            namedTextures["FluidDye"] = fluid.Dye;
            if (fluid.Age != null) namedTextures["FluidAge"] = fluid.Age;
            namedSamplers["FluidSampler"] = fluid.Sampler;
            context.RSSetViewport(0, 0, width, height);
        }
        if (active.Program.Uses("PreviousImage"))
        {
            feedback ??= new(device, context, width, height);
            if (previousScene != SceneName || dt > .5) feedback.Clear();
            GpuMarker("Feedback", true);
            feedback.Advance(dt, active, namedBuffers, namedTextures, namedSamplers);
            GpuMarker("Feedback", false);
        }
        previousScene = SceneName;
        if (active.Distance != null)
        {
            distanceField ??= new(device, context, width, height);
            GpuMarker("Distance field", true);
            distanceField.Update(active.Distance, active.Parameters!, namedBuffers);
            GpuMarker("Distance field", false);
            namedTextures["DistanceField"] = distanceField.Field;
            namedSamplers["DistanceSampler"] = distanceField.Sampler;
            context.RSSetViewport(0, 0, width, height);
        }
        context.OMSetRenderTargets(output.SceneTarget);
        GpuMarker("Scene", true);
        (active.Composite ?? active.Program).Bind(context, active.Parameters, namedBuffers, namedTextures, namedSamplers);
        if (active.FourierShadow != null) GpuMarker("Fourier camera", true);
        if (active.Raster != null) GpuMarker("Raster composite", true);
        context.Draw(3, 0);
        if (active.Raster != null) GpuMarker("Raster composite", false);
        if (active.FourierShadow != null) GpuMarker("Fourier camera", false);
        GpuMarker("Scene", false);
        GpuMarker("Output", true);
        output.Draw(outputPixel, now);
        GpuMarker("Output", false);
        gpu.EndFrame();
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
    struct Frame { public Vector4 A, B, C, D, E, F; }


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

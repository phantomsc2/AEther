using AEther.NAudio;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.System;
using AEther.Logging;
using Serilog;

namespace AEther.WinUI;

public sealed class MainWindow : Window
{
    readonly SwapChainPanel canvas = new();
    readonly UserControl root;
    readonly SettingsPanel settings;
    readonly TitleBar? hubTitleBar;
    readonly AnalysisView analysis;
    readonly FrameTimeView frameTime;
    readonly CanvasWindow canvasWindow;
    readonly AudioPanel audio;
    readonly GraphicsPanel graphics = new();
    readonly ShaderPanel shaders = new();
    readonly SemaphoreSlim sessionGate = new(1, 1);
    readonly DispatcherTimer reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer timingTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer analyzerEditTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly Func<SampleSource>? createSource;
    readonly string? shaderDirectory;
    readonly SettingsStore? settingsStore;
    readonly AppSettings appSettings;
    Session? session;
    RenderWorker? renderWorker;
    FileSystemWatcher? watcher;
    IDXGISwapChain1? canvasChain;
    bool loaded, closing, shutdownComplete, fullscreen, visible;
    bool changingSession, renderCanvas, renderDebug, canvasDismissed;
    bool analyzerSettingsDirty;
    long restartVersion;
    RectInt32 windowBounds;
    PipelineWarnings? pipelineWarnings;
    DateTimeOffset nextHealthSample;
    bool? presentationEstimated;
    ILogger Logger => session?.Logger ?? Log.ForContext<MainWindow>();

    public MainWindow(Func<SampleSource>? createSource = null, string? shaderDirectory = null)
    {
        ApplicationLog.Initialize();
        this.createSource = createSource;
        this.shaderDirectory = shaderDirectory;
        // Injected sources and shader directories are used by development probes.
        settingsStore = createSource == null && shaderDirectory == null ? new SettingsStore() : null;
        try { appSettings = settingsStore?.Load() ?? new(); }
        catch (Exception error)
        {
            Log.ForContext<MainWindow>().Warning(error, "Could not load settings; using defaults");
            appSettings = new();
        }
        audio = new AudioPanel(appSettings.Analyzer);
        graphics.SetOptions(appSettings.Graphics);
        Title = "AEther";
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        canvasDismissed = !Environment.GetCommandLineArgs().Any(argument => argument.StartsWith("--scene=", StringComparison.Ordinal));
        // A focusable host routes keyboard input without adding anything over the canvas.
        root = new UserControl { Content = canvas, IsTabStop = true, UseSystemFocusVisuals = false,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        canvasWindow = new(root);
        analysis = new(this);
        frameTime = new(this);
        settings = new(audio, graphics, shaders, analysis, frameTime);
        SystemBackdrop = new MicaBackdrop();
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            hubTitleBar = new TitleBar
            {
                Title = Title,
            };
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.Children.Add(hubTitleBar);
            Grid.SetRow(settings, 1);
            layout.Children.Add(settings);
            Content = layout;
            ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            SetTitleBar(hubTitleBar);
        }
        else Content = settings;
        settings.Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            InitializeHubSize();
            // Place the initial canvas beside the hub when there is room. Subsequent
            // moves, edits and restarts leave the user's window placement alone.
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var right = AppWindow.Position.X + AppWindow.Size.Width + 16;
            var left = AppWindow.Position.X - canvasWindow.AppWindow.Size.Width - 16;
            var x = right + canvasWindow.AppWindow.Size.Width <= area.X + area.Width ? right
                : left >= area.X ? left : area.X;
            canvasWindow.AppWindow.Move(new PointInt32(x, Math.Clamp(AppWindow.Position.Y,
                area.Y, Math.Max(area.Y, area.Y + area.Height - canvasWindow.AppWindow.Size.Height))));
            await RestartAsync();
            if (!closing && !canvasDismissed && session != null) canvasWindow.Show();
        };
        canvasWindow.SetOwner(this);
        shaders.SceneChanged += (_, _) => RefreshScenePicker();
        canvasWindow.Dismissed += (_, _) =>
        {
            if (closing) return;
            canvasDismissed = true;
            shaders.SelectScene(null);
            UpdateRendering();
            RefreshScenePicker();
        };
        settings.SceneSelected += name =>
        {
            try
            {
                if (session?.Renderer.SceneName != name) shaders.SelectScene(name);
                OpenCanvas();
            }
            catch (Exception error)
            {
                RefreshScenePicker();
                shaders.SetStatus(error.Message);
                Logger.Error(error, "Could not select scene {Scene}", name);
            }
        };
        settings.InputChanged += async (_, _) => await RestartCoreAsync(preserveGraphics: true);
        audio.SettingsChanged += (_, _) =>
        {
            analyzerSettingsDirty = true;
            // Invalidate queued restarts and prevent an older generation from saving
            // over edits that are still settling in the controls.
            restartVersion++;
            analyzerEditTimer.Stop();
            analyzerEditTimer.Start();
        };
        analyzerEditTimer.Tick += async (_, _) =>
        {
            analyzerEditTimer.Stop();
            await RestartCoreAsync(preserveGraphics: true);
        };
        graphics.SettingsChanged += (_, _) =>
        {
            try
            {
                session?.Renderer.SetGraphicsOptions(graphics.Options);
                Logger.Information("Graphics configuration changed to {@GraphicsOptions}", graphics.Options);
                appSettings.Graphics = graphics.Options;
                SaveSettings();
            }
            catch (Exception error)
            {
                graphics.SetOptions(session?.Renderer.Options ?? new());
                Logger.Error(error, "Could not apply graphics configuration");
            }
        };
        ToolWindow.AddShortcut(settings, VirtualKey.F11, ToggleFullscreen);
        ToolWindow.AddShortcut(root, VirtualKey.F11, ToggleFullscreen);
        root.DoubleTapped += (_, e) => { ToggleFullscreen(); e.Handled = true; };
        canvas.Loaded += (_, _) =>
        {
            SwapChainHost.Attach(canvas, canvasChain);
            ResizeRenderer();
        };
        canvasWindow.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && loaded)
                root.Focus(FocusState.Programmatic);
        };
        canvas.SizeChanged += (_, _) => ResizeRenderer();
        canvas.CompositionScaleChanged += (_, _) => ResizeRenderer();
        reloadTimer.Tick += (_, _) =>
        {
            reloadTimer.Stop();
            if (session == null || closing) return;
            try
            {
                var changedPath = pendingShaderPath;
                pendingShaderPath = null;
                var sceneDirectory = Path.Combine(session.Renderer.ShaderDirectory, "Scenes");
                var sameScenes = Directory.EnumerateFiles(sceneDirectory, "*.hlsl")
                    .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(session.Renderer.Scenes.Select(scene => scene.Name));
                if (!sameScenes) shaders.Reload();
                else if (changedPath != null && Path.GetDirectoryName(changedPath) == sceneDirectory)
                {
                    session.Renderer.ReloadScene(Path.GetFileNameWithoutExtension(changedPath));
                    shaders.RefreshParameters();
                }
                else if (changedPath != null && session.Renderer.ReloadActiveScene()) shaders.RefreshParameters();
                else shaders.Reload();
                analysis.RefreshOutput(resetError: true);
                if (session.Renderer.ShaderError is { } error) shaders.SetStatus(error);
            }
            catch (Exception error) { Logger.Error(error, "Could not reload changed shaders"); }
        };
        // A Tick already queued before Stop can still arrive during window teardown.
        timingTimer.Tick += (_, _) =>
        {
            if (closing) return;
            SamplePipelineHealth();
            UpdateRendering();
        };
        timingTimer.Start();
        canvasWindow.VisibilityChanged += (_, e) => { visible = e.Visible; UpdateRendering(); };
        canvasWindow.AppWindow.Changed += (_, _) => UpdateRendering();
        analysis.OutputChanged += (_, _) => UpdateRendering();
        AppWindow.Closing += OnClosing;
    }

    void InitializeHubSize()
    {
        var scale = settings.XamlRoot.RasterizationScale;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var chrome = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        var available = work.Y + work.Height - Math.Max(work.Y, AppWindow.Position.Y) - chrome;
        AppWindow.ResizeClient(new SizeInt32(Math.Min((int)Math.Ceiling(1280 * scale), work.Width),
            Math.Min((int)Math.Ceiling(720 * scale), Math.Max(1, available))));
    }

    void AttachCanvasSwapChain(IDXGISwapChain1? chain)
    {
        canvasChain = chain;
        if (canvas.IsLoaded || chain == null) SwapChainHost.Attach(canvas, chain);
    }

    (int Width, int Height) CanvasSize =>
        (canvas.ActualWidth > 0 ? Math.Max(1, (int)Math.Round(canvas.ActualWidth * canvas.CompositionScaleX)) : 1280,
         canvas.ActualHeight > 0 ? Math.Max(1, (int)Math.Round(canvas.ActualHeight * canvas.CompositionScaleY)) : 720);

    void ResizeRenderer()
    {
        if (closing || session == null || canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        try
        {
            var size = CanvasSize;
            session.Renderer.Resize(size.Width, size.Height);
            session.Renderer.SetCompositionScale(canvas.CompositionScaleX, canvas.CompositionScaleY);
        }
        catch (Exception error) { RenderFailed(error); }
    }

    Task RestartAsync() => RestartCoreAsync(preserveGraphics: false);

    SampleSource CreateSource() => createSource?.Invoke() ?? (settings.InputName == SettingsPanel.Loopback
        ? new Loopback() : new Recorder(settings.InputName));

    async Task RestartCoreAsync(bool preserveGraphics)
    {
        analyzerEditTimer.Stop();
        if (closing || !loaded) return;
        var version = ++restartVersion;
        await sessionGate.WaitAsync();
        changingSession = true;
        long pipelineId = 0;
        ILogger attemptLogger = Logger;
        try
        {
            if (closing || version != restartVersion) return;
            pipelineId = ApplicationLog.Events.BeginPipeline();
            attemptLogger = Log.ForContext<MainWindow>().ForContext("PipelineId", pipelineId);
            attemptLogger.Information("Starting audio pipeline with input {Input}; retaining graphics {RetainGraphics}; options {@Options}",
                settings.InputName, preserveGraphics, audio.Options);
            if (preserveGraphics && session is { } current)
            {
                renderWorker?.Dispose(); renderWorker = null;
                SampleSource? replacement = null;
                try
                {
                    replacement = CreateSource();
                    await current.RestartAnalysisAsync(replacement, audio.Options, attemptLogger, pipelineId);
                    current.Analyzer.Profile.DetailedGpuTimings = false;
                    frameTime.Attach(current.Analyzer.Profile);
                    replacement = null; // Session now owns the new source.
                    if (closing) return;
                    ResizeRenderer();
                    analysis.RefreshOutput();
                    current.Start();
                    StartRenderWorker(current);
                    PipelineStarted(current);
                    SaveAcceptedAnalyzerSettings(current, version);
                    UpdateRendering();
                }
                catch
                {
                    replacement?.Dispose();
                    // Keep graphics available for the next valid settings edit.
                    // If Start failed after the swap, stop that generation too.
                    if (replacement == null) await current.StopAsync();
                    StartRenderWorker(current);
                    UpdateRendering();
                    throw;
                }
                return;
            }
            await StopSessionAsync();
            if (closing || version != restartVersion) return;
            SampleSource source = CreateSource();
            Session? next = null;
            try
            {
                var size = CanvasSize;
                next = new(WinRT.Interop.WindowNative.GetWindowHandle(canvasWindow), size.Width, size.Height,
                    source, audio.Options, shaderDirectory, AttachCanvasSwapChain, shaders.SelectedScene, graphics.Options, attemptLogger, pipelineId);
                next.Renderer.SetCompositionScale(canvas.CompositionScaleX, canvas.CompositionScaleY);
                next.CaptureStopped += CaptureStopped;
                next.Start();
                session = next;
                next.Analyzer.Profile.DetailedGpuTimings = false;
                frameTime.Attach(next.Analyzer.Profile);
                analysis.Attach(next.Renderer);
                shaders.Attach(next.Renderer);
                watcher = new(next.Renderer.ShaderDirectory, "*.hlsl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Changed += ShaderChanged;
                watcher.Created += ShaderChanged;
                watcher.Renamed += ShaderChanged;
                watcher.Deleted += ShaderChanged;
                watcher.Error += WatcherFailed;
                watcher.EnableRaisingEvents = true;
                StartRenderWorker(next);
                PipelineStarted(next);
                SaveAcceptedAnalyzerSettings(next, version);
                UpdateRendering();
            }
            catch
            {
                renderWorker?.Dispose(); renderWorker = null;
                session = null;
                frameTime.Attach(null);
                analysis.Attach(null);
                shaders.Attach(null);
                if (next != null) await next.DisposeAsync();
                else source.Dispose();
                throw;
            }
        }
        catch (Exception error)
        {
            attemptLogger.ForContext("PipelineBlocking", session?.IsRunning != true)
                .Error(error, "Could not start audio pipeline");
        }
        finally { changingSession = false; sessionGate.Release(); UpdateRendering(); }
    }

    void SaveAcceptedAnalyzerSettings(Session current, long version)
    {
        if (!analyzerSettingsDirty || version != restartVersion) return;
        analyzerSettingsDirty = false;
        appSettings.Analyzer = current.Analyzer.Options.Copy();
        SaveSettings();
    }

    void SaveSettings()
    {
        try { settingsStore?.Save(appSettings); }
        catch (Exception error) { Logger.Warning(error, "Could not save settings"); }
    }

    void CaptureStopped(object? sender, Exception? error)
    {
        if (error == null || closing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !ReferenceEquals(sender, session)) return;
            // Session logs the original exception on the failing thread.
            UpdateRendering();
        });
    }

    void ShaderChanged(object sender, FileSystemEventArgs e)
    {
        if (closing) return;
        pendingShaderPath = e.FullPath;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !ReferenceEquals(sender, watcher)) return;
            reloadTimer.Stop(); reloadTimer.Start();
        });
    }

    void StartRenderWorker(Session current)
    {
        renderWorker?.Dispose();
        renderCanvas = CanvasVisible;
        current.Renderer.SetCanvasProgressActive(renderCanvas);
        renderDebug = analysis.CanRender;
        var drawCanvas = renderCanvas;
        var drawDebug = renderDebug;
        var analyzer = current.Analyzer;
        var logger = current.Logger;
        // Present(1) can follow the slower primary monitor. On Windows 11 use
        // compositor cadence with Present(0), retaining swap-chain backpressure
        // before sampling audio rather than stalling on the output draw afterward.
        bool compositorPacing = CompositorClock.IsSupported;
        renderWorker = new(!drawCanvas && drawDebug ? current.Renderer.DebugFrameReady! : current.Renderer.FrameReady,
            () => current.Renderer.Render(syncInterval: compositorPacing ? 0u : 1u,
                renderCanvas: drawCanvas, renderDebug: drawDebug),
            error =>
            {
                logger.ForContext("PipelineBlocking", true).Error(error, "Rendering failed; stopping audio pipeline");
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!closing && ReferenceEquals(session, current) && ReferenceEquals(current.Analyzer, analyzer))
                        _ = StopAfterRenderFailureAsync(current, current.PipelineId);
                });
            },
            waitForTick: compositorPacing ? CompositorClock.Wait : null,
            waited: milliseconds => analyzer.Profile.Record("render.pacing_wait_ms", milliseconds));
    }

    string? pendingShaderPath;

    void PipelineStarted(Session current)
    {
        pipelineWarnings = new(current.Logger);
        presentationEstimated = null;
        nextHealthSample = DateTimeOffset.MinValue;
        var format = current.Analyzer.Format;
        current.Logger.Information("Audio configured: {SampleRate} Hz, {Channels} channels, analysis {AnalysisRate} Hz; history delay {HistoryDelayMilliseconds} ms; graphics {@GraphicsOptions}",
            format.SampleRate, format.ChannelCount,
            format.SampleRate / (double)(format.SampleRate / current.Analyzer.Options.TimeResolution),
            current.Renderer.Audio.PresentationDelay * 1000, current.Renderer.Options);
        foreach (var stage in new[] { "startup.analysis_ms", "startup.dft_ms", "startup.smoother_ms", "startup.splitter_ms",
            "startup.gpu_ms", "startup.gpu_device_ms", "startup.gpu_output_ms", "startup.gpu_shaders_ms", "startup.gpu_audio_ms" })
            if (current.Analyzer.Profile.Recent(stage) is { } timing)
                current.Logger.Information("Startup stage {Stage} took {Milliseconds} ms", stage, timing.Mean);
        if (current.IsRunning)
        {
            current.Logger.Information("Audio pipeline started; graphics programs retained {GraphicsRetained}",
                !current.Analyzer.Profile.Recent("startup.gpu_ms").HasValue);
        }
    }

    void SamplePipelineHealth()
    {
        if (changingSession || session is not { IsRunning: true } current || pipelineWarnings == null) return;
        var now = DateTimeOffset.UtcNow;
        if (now < nextHealthSample) return;
        nextHealthSample = now.AddSeconds(1);
        var analyzer = current.Analyzer;
        pipelineWarnings.Sample(analyzer.DroppedInputCount, analyzer.DroppedAudioMilliseconds,
            current.Renderer.Audio.DroppedRows, analyzer.LastTimestampQuality);
        var estimated = current.Renderer.LastPresentationTimestamp.HasValue;
        if (presentationEstimated != estimated)
        {
            if (presentationEstimated == true && !estimated)
                current.Logger.Warning("Presentation clock estimate unavailable; using host clock");
            else current.Logger.Information("Presentation timing uses {Clock}", estimated ? "compositor estimate" : "host clock");
            presentationEstimated = estimated;
        }
    }

    void WatcherFailed(object sender, ErrorEventArgs e)
    {
        var error = e.GetException();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !ReferenceEquals(sender, watcher)) return;
            // The watcher survives audio-only restarts, so this is an application event.
            Log.ForContext<MainWindow>().Warning(error, "Shader file watcher failed; automatic reload may be unavailable");
        });
    }

    async Task StopSessionAsync()
    {
        reloadTimer.Stop();
        watcher?.Dispose(); watcher = null;
        var previous = session; session = null;
        frameTime.Attach(null);
        renderWorker?.Dispose(); renderWorker = null;
        analysis.Attach(null);
        shaders.Attach(null);
        pipelineWarnings = null;
        if (previous == null) return;
        previous.CaptureStopped -= CaptureStopped;
        await previous.DisposeAsync();
    }

    void ToggleFullscreen()
    {
        if (canvasDismissed) return;
        if (!canvasWindow.IsVisible) OpenCanvas();
        if (!fullscreen)
        {
            windowBounds = new RectInt32(canvasWindow.AppWindow.Position.X, canvasWindow.AppWindow.Position.Y, canvasWindow.AppWindow.Size.Width, canvasWindow.AppWindow.Size.Height);
            canvasWindow.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            fullscreen = true;
        }
        else
        {
            fullscreen = false;
            canvasWindow.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            canvasWindow.AppWindow.MoveAndResize(windowBounds);
        }
    }

    void RefreshScenePicker()
    {
        settings.SetScenes(session?.Renderer.Scenes.Select(scene => scene.Name) ?? [],
            canvasDismissed ? null : shaders.SelectedScene);
    }

    void OpenCanvas()
    {
        canvasDismissed = false;
        canvasWindow.Show();
        RefreshScenePicker();
    }

    bool CanvasVisible => !canvasDismissed && visible && canvasWindow.IsVisible && canvasWindow.AppWindow.Presenter is not OverlappedPresenter
        { State: OverlappedPresenterState.Minimized };

    void UpdateRendering()
    {
        if (closing || changingSession || session == null)
        {
            renderWorker?.SetEnabled(false);
            return;
        }
        if (renderWorker == null || renderCanvas != CanvasVisible || renderDebug != analysis.CanRender)
            StartRenderWorker(session);
        renderWorker?.SetEnabled(renderCanvas || renderDebug);
    }

    void RenderFailed(Exception error)
    {
        Logger.ForContext("PipelineBlocking", true).Error(error, "Rendering failed; stopping audio pipeline");
        _ = StopAfterRenderFailureAsync(session, session?.PipelineId ?? 0);
    }

    async Task StopAfterRenderFailureAsync(Session? failed, long pipelineId)
    {
        await sessionGate.WaitAsync();
        try { if (ReferenceEquals(session, failed) && session?.PipelineId == pipelineId) await StopSessionAsync(); }
        catch (Exception error) { Log.Error(error, "Could not stop failed pipeline"); }
        finally { sessionGate.Release(); }
    }

    void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (shutdownComplete) return;
        e.Cancel = true;
        RequestClose();
    }

    public void RequestClose() => _ = ShutdownAsync();

    async Task ShutdownAsync()
    {
        if (closing) return;
        closing = true;
        analyzerEditTimer.Stop();
        UpdateRendering();
        timingTimer.Stop();
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { Log.Error(error, "Could not stop pipeline during shutdown"); }
        finally
        {
            sessionGate.Release();
            canvasWindow.Destroy();
            shutdownComplete = true;
            // Closing can finish synchronously; do not reenter WinUI's Closing event.
            DispatcherQueue.TryEnqueue(() => base.Close());
        }
    }
}


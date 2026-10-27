using AEther.NAudio;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.System;

namespace AEther.WinUI;

public sealed class MainWindow : Window
{
    readonly SwapChainPanel canvas = new();
    readonly UserControl root;
    readonly SettingsPanel settings;
    readonly AnalysisView analysis;
    readonly FrameTimeView frameTime;
    readonly CanvasWindow canvasWindow;
    readonly AudioPanel audio = new();
    readonly GraphicsPanel graphics = new();
    readonly DebugPanel debug = new();
    readonly ShaderPanel shaders = new();
    readonly SemaphoreSlim sessionGate = new(1, 1);
    readonly DispatcherTimer reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer timingTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Func<SampleSource>? createSource;
    readonly string? shaderDirectory;
    Session? session;
    RenderWorker? renderWorker;
    FileSystemWatcher? watcher;
    IDXGISwapChain1? canvasChain;
    bool loaded, closing, shutdownComplete, fullscreen, visible;
    bool changingSession, renderCanvas, renderDebug, canvasDismissed;
    long restartVersion;
    RectInt32 windowBounds;

    public MainWindow(Func<SampleSource>? createSource = null, string? shaderDirectory = null)
    {
        this.createSource = createSource;
        this.shaderDirectory = shaderDirectory;
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
        settings = new(audio, graphics, shaders, debug, analysis, frameTime);
        Content = settings;
        settings.ActualThemeChanged += (_, _) => UpdateTitleBarColors();
        UpdateTitleBarColors();
        settings.Loaded += async (_, _) =>
        {
            UpdateTitleBarColors();
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
        if (settings.DeviceError is { } error) { debug.SetStatus(error); settings.ShowDiagnostics(); }
        canvasWindow.SetOwner(this);
        shaders.SceneChanged += (_, _) => RefreshScenePicker();
        canvasWindow.Dismissed += (_, _) =>
        {
            if (closing) return;
            canvasDismissed = true;
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
                debug.SetStatus(error.Message);
                settings.ShowDiagnostics();
            }
        };
        settings.InputChanged += async (_, _) => await RestartCoreAsync(preserveGraphics: true);
        settings.DiagnosticsRequested += (_, _) =>
        {
            if (session != null) session.Analyzer.Profile.DetailedGpuTimings = true;
            debug.SetTiming(session);
        };
        settings.Diagnostics.Collapsed += (_, _) =>
        {
            if (session != null) session.Analyzer.Profile.DetailedGpuTimings = false;
        };
        audio.SettingsChanged += async (_, _) => await RestartCoreAsync(preserveGraphics: true);
        graphics.SettingsChanged += (_, _) =>
        {
            try { session?.Renderer.SetGraphicsOptions(graphics.Options); }
            catch (Exception error)
            {
                graphics.SetOptions(session?.Renderer.Options ?? new());
                debug.SetStatus(error.Message);
                settings.ShowDiagnostics();
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
            if (session.Renderer.ShaderError is { } error)
            {
                shaders.SetStatus(error);
                debug.SetStatus(error);
                settings.ShowDiagnostics();
            }
        };
        // A Tick already queued before Stop can still arrive during window teardown.
        timingTimer.Tick += (_, _) =>
        {
            if (closing) return;
            if (analysis.Error is { } error) debug.SetStatus($"Analysis view: {error}");
            if (settings.DiagnosticsExpanded && AppWindow.IsVisible
                && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }) debug.SetTiming(session);
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
        AppWindow.ResizeClient(new SizeInt32(Math.Min((int)Math.Ceiling(720 * scale), work.Width),
            Math.Min((int)Math.Ceiling(680 * scale), Math.Max(1, available))));
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
        if (closing || !loaded) return;
        var version = ++restartVersion;
        await sessionGate.WaitAsync();
        changingSession = true;
        try
        {
            if (closing || version != restartVersion) return;
            if (preserveGraphics && session is { } current)
            {
                renderWorker?.Dispose(); renderWorker = null;
                SampleSource? replacement = null;
                try
                {
                    replacement = CreateSource();
                    await current.RestartAnalysisAsync(replacement, audio.Options);
                    current.Analyzer.Profile.DetailedGpuTimings = settings.DiagnosticsExpanded;
                    frameTime.Attach(current.Analyzer.Profile);
                    replacement = null; // Session now owns the new source.
                    if (closing) return;
                    ResizeRenderer();
                    analysis.RefreshOutput();
                    current.Start();
                    StartRenderWorker(current);
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
                    source, audio.Options, shaderDirectory, AttachCanvasSwapChain, shaders.SelectedScene, graphics.Options);
                next.Renderer.SetCompositionScale(canvas.CompositionScaleX, canvas.CompositionScaleY);
                next.CaptureStopped += CaptureStopped;
                next.Start();
                session = next;
                next.Analyzer.Profile.DetailedGpuTimings = settings.DiagnosticsExpanded;
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
                watcher.EnableRaisingEvents = true;
                debug.SetStatus("Running. F11 or double-click canvas: fullscreen.");
                StartRenderWorker(next);
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
        catch (Exception error) { debug.SetStatus(error.Message); settings.ShowDiagnostics(); }
        finally { changingSession = false; sessionGate.Release(); UpdateRendering(); }
    }

    void CaptureStopped(object? sender, Exception? error)
    {
        if (error == null || closing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !ReferenceEquals(sender, session)) return;
            debug.SetStatus(error.Message);
            settings.ShowDiagnostics();
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
        renderDebug = analysis.CanRender;
        var drawCanvas = renderCanvas;
        var drawDebug = renderDebug;
        // Present(1) can follow the slower primary monitor. On Windows 11 use
        // compositor cadence with Present(0), retaining swap-chain backpressure
        // before sampling audio rather than stalling on the output draw afterward.
        bool compositorPacing = CompositorClock.IsSupported;
        renderWorker = new(!drawCanvas && drawDebug ? current.Renderer.DebugFrameReady! : current.Renderer.FrameReady,
            () => current.Renderer.Render(syncInterval: compositorPacing ? 0u : 1u,
                renderCanvas: drawCanvas, renderDebug: drawDebug),
            error => DispatcherQueue.TryEnqueue(() => RenderFailed(error)),
            waitForTick: compositorPacing ? CompositorClock.Wait : null,
            waited: milliseconds => current.Analyzer.Profile.Record("render.pacing_wait_ms", milliseconds));
    }

    string? pendingShaderPath;

    async Task StopSessionAsync()
    {
        reloadTimer.Stop();
        watcher?.Dispose(); watcher = null;
        var previous = session; session = null;
        frameTime.Attach(null);
        renderWorker?.Dispose(); renderWorker = null;
        analysis.Attach(null);
        shaders.Attach(null);
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
        Title = canvasDismissed ? "AEther" : $"AEther - {shaders.SelectedScene}";
        settings.SetScenes(session?.Renderer.Scenes.Select(scene => scene.Name) ?? [],
            canvasDismissed ? null : shaders.SelectedScene);
    }

    void OpenCanvas()
    {
        canvasDismissed = false;
        canvasWindow.Show();
        RefreshScenePicker();
    }

    bool CanvasVisible => visible && canvasWindow.AppWindow.Presenter is not OverlappedPresenter
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
        debug.SetStatus(error.Message);
        settings.ShowDiagnostics();
        _ = StopAfterRenderFailureAsync();
    }

    async Task StopAfterRenderFailureAsync()
    {
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { if (!closing) debug.SetStatus(error.Message); }
        finally { sessionGate.Release(); }
    }

    void UpdateTitleBarColors()
    {
        var background = (SolidColorBrush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        settings.Columns.Background = background;
        var foreground = settings.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var titleBar = AppWindow.TitleBar;
        titleBar.BackgroundColor = titleBar.InactiveBackgroundColor = background.Color;
        titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = background.Color;
        titleBar.ForegroundColor = titleBar.ButtonForegroundColor = foreground;
        titleBar.InactiveForegroundColor = titleBar.ButtonInactiveForegroundColor = Blend(.6);
        titleBar.ButtonHoverBackgroundColor = Blend(.08);
        titleBar.ButtonPressedBackgroundColor = Blend(.05);
        titleBar.ButtonHoverForegroundColor = titleBar.ButtonPressedForegroundColor = foreground;

        Windows.UI.Color Blend(double amount) => Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(background.Color.R + (foreground.R - background.Color.R) * amount),
            (byte)Math.Round(background.Color.G + (foreground.G - background.Color.G) * amount),
            (byte)Math.Round(background.Color.B + (foreground.B - background.Color.B) * amount));
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
        UpdateRendering();
        timingTimer.Stop();
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
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

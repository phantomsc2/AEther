using AEther.NAudio;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.System;

namespace AEther.WinUI;

public sealed class MainWindow : Window
{
    readonly SwapChainPanel canvas = new();
    readonly UserControl root;
    readonly SettingsPanel settings;
    readonly CanvasWindow canvasWindow;
    readonly AudioWindow audio = new();
    readonly DebugWindow debug = new();
    readonly ShaderWindow shaders = new();
    readonly SemaphoreSlim sessionGate = new(1, 1);
    readonly DispatcherTimer reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer timingTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Func<SampleSource>? createSource;
    readonly string? shaderDirectory;
    Session? session;
    RenderWorker? renderWorker;
    FileSystemWatcher? watcher;
    bool loaded, closing, shutdownComplete, fullscreen, visible = true;
    long restartVersion;
    RectInt32 windowBounds;

    public MainWindow(Func<SampleSource>? createSource = null, string? shaderDirectory = null)
    {
        this.createSource = createSource;
        this.shaderDirectory = shaderDirectory;
        Title = "AEther";
        // A focusable host routes keyboard input without adding anything over the canvas.
        root = new UserControl { Content = canvas, IsTabStop = true, UseSystemFocusVisuals = false };
        canvasWindow = new(root);
        settings = new(audio, shaders, debug, canvasWindow);
        Content = settings;
        settings.Loaded += (_, _) =>
        {
            settings.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var scale = settings.XamlRoot.RasterizationScale;
            AppWindow.ResizeClient(new SizeInt32((int)Math.Ceiling(settings.DesiredSize.Width * scale),
                (int)Math.Ceiling(settings.DesiredSize.Height * scale)));
            // Place the initial canvas beside the hub when there is room. Subsequent
            // moves, edits and restarts leave the user's window placement alone.
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var right = AppWindow.Position.X + AppWindow.Size.Width + 16;
            var left = AppWindow.Position.X - canvasWindow.AppWindow.Size.Width - 16;
            var x = right + canvasWindow.AppWindow.Size.Width <= area.X + area.Width ? right
                : left >= area.X ? left : area.X;
            canvasWindow.AppWindow.Move(new PointInt32(x, Math.Clamp(AppWindow.Position.Y,
                area.Y, Math.Max(area.Y, area.Y + area.Height - canvasWindow.AppWindow.Size.Height))));
            canvasWindow.AppWindow.Show(false);
        };
        foreach (var window in new ToolWindow[] { audio, debug })
        {
            window.SetOwner(this);
            window.ShortcutRequested += HandleToolShortcut;
        }
        if (settings.DeviceError is { } error) { debug.SetStatus(error); debug.Show(); }
        canvasWindow.SetOwner(this);
        shaders.SetOwner(this);
        shaders.SceneChanged += (_, _) =>
        {
            Title = $"AEther - {shaders.SelectedScene}";
            settings.SetScenes(session?.Renderer.Scenes.Select(scene => scene.Name) ?? [], shaders.SelectedScene);
        };
        settings.SceneSelected += name =>
        {
            if (session?.Renderer.SceneName != name) shaders.SelectScene(name);
        };
        settings.InputChanged += async (_, _) => await RestartCoreAsync(preserveGraphics: true);
        audio.SettingsChanged += async (_, _) => await RestartCoreAsync(preserveGraphics: true);
        foreach (var key in new[] { VirtualKey.F5, VirtualKey.F6, VirtualKey.F11 })
            ToolWindow.AddShortcut(settings, key, () => HandleToolShortcut(key));
        shaders.ShortcutRequested += key =>
        {
            if (key == VirtualKey.F6) shaders.Hide();
            else HandleToolShortcut(key);
        };
        ToolWindow.AddShortcut(root, VirtualKey.Space, Activate);
        ToolWindow.AddShortcut(root, VirtualKey.F6, shaders.Toggle);
        ToolWindow.AddShortcut(root, VirtualKey.F11, ToggleFullscreen);
        ToolWindow.AddShortcut(root, VirtualKey.F5, () => _ = RestartAsync());
        ToolWindow.AddShortcut(root, VirtualKey.Escape, canvasWindow.Hide);
        ToolWindow.AddShortcut(root, VirtualKey.R, () => { if (session != null) { session.Renderer.CombinedLevel ^= true; shaders.RefreshParameters(); } });
        ToolWindow.AddShortcut(root, VirtualKey.I, () => { if (session != null) { session.Renderer.ShowSum ^= true; shaders.RefreshParameters(); } });
        ToolWindow.AddShortcut(root, (VirtualKey)219, () => ChangeSlice(-.05f));
        ToolWindow.AddShortcut(root, (VirtualKey)221, () => ChangeSlice(.05f));
        canvas.Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            root.Focus(FocusState.Programmatic);
            await RestartAsync();
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
            if (session.Renderer.ShaderError is { } error)
            {
                shaders.SetStatus(error);
                shaders.Show();
            }
        };
        // A Tick already queued before Stop can still arrive during window teardown.
        timingTimer.Tick += (_, _) => { if (!closing && debug.IsVisible) debug.SetTiming(session); };
        timingTimer.Start();
        canvasWindow.VisibilityChanged += (_, e) => { visible = e.Visible; UpdateRendering(); };
        canvasWindow.AppWindow.Changed += (_, _) => UpdateRendering();
        AppWindow.Closing += OnClosing;
    }

    void HandleToolShortcut(VirtualKey key)
    {
        if (key == VirtualKey.F6) shaders.Toggle();
        if (key == VirtualKey.F11) ToggleFullscreen();
        if (key == VirtualKey.F5) _ = RestartAsync();
    }

    void ChangeSlice(float change)
    {
        if (session == null) return;
        session.Renderer.SliceDepth += change;
        shaders.RefreshParameters();
    }

    (int Width, int Height) CanvasSize =>
        (Math.Max(1, (int)Math.Round(canvas.ActualWidth * canvas.CompositionScaleX)),
         Math.Max(1, (int)Math.Round(canvas.ActualHeight * canvas.CompositionScaleY)));

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
                    replacement = null; // Session now owns the new source.
                    if (closing) return;
                    ResizeRenderer();
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
                    source, audio.Options, shaderDirectory, chain => SwapChainHost.Attach(canvas, chain), shaders.SelectedScene);
                next.Renderer.SetCompositionScale(canvas.CompositionScaleX, canvas.CompositionScaleY);
                next.CaptureStopped += CaptureStopped;
                next.Start();
                session = next;
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
                debug.SetStatus("Running. Space: settings · F6: scene · F11: fullscreen\nF5: restart · R: level · I: sum · [ / ]: slice");
                StartRenderWorker(next);
                UpdateRendering();
            }
            catch
            {
                renderWorker?.Dispose(); renderWorker = null;
                session = null;
                shaders.Attach(null);
                if (next != null) await next.DisposeAsync();
                else source.Dispose();
                throw;
            }
        }
        catch (Exception error) { debug.SetStatus(error.Message); debug.Show(); }
        finally { sessionGate.Release(); }
    }

    void CaptureStopped(object? sender, Exception? error)
    {
        if (error == null || closing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !ReferenceEquals(sender, session)) return;
            debug.SetStatus(error.Message);
            debug.Show();
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
        // Present(1) can follow the slower primary monitor. On Windows 11 use
        // compositor cadence with Present(0), retaining swap-chain backpressure
        // before sampling audio rather than stalling on the output draw afterward.
        bool compositorPacing = CompositorClock.IsSupported;
        renderWorker = new(current.Renderer.FrameReady,
            () => current.Renderer.Render(syncInterval: compositorPacing ? 0u : 1u),
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
        renderWorker?.Dispose(); renderWorker = null;
        shaders.Attach(null);
        if (previous == null) return;
        previous.CaptureStopped -= CaptureStopped;
        await previous.DisposeAsync();
    }

    void ToggleFullscreen()
    {
        if (!canvasWindow.IsVisible) canvasWindow.Show();
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

    void UpdateRendering()
    {
        renderWorker?.SetEnabled(!closing && visible && session != null
            && canvasWindow.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized });
    }

    void RenderFailed(Exception error)
    {
        debug.SetStatus(error.Message);
        debug.Show();
        _ = StopAfterRenderFailureAsync();
    }

    async Task StopAfterRenderFailureAsync()
    {
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { if (!closing) debug.SetStatus(error.Message); }
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
        UpdateRendering();
        timingTimer.Stop();
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        finally
        {
            sessionGate.Release();
            shaders.Destroy();
            canvasWindow.Destroy();
            audio.Destroy();
            debug.Destroy();
            shutdownComplete = true;
            // Closing can finish synchronously; do not reenter WinUI's Closing event.
            DispatcherQueue.TryEnqueue(() => base.Close());
        }
    }
}

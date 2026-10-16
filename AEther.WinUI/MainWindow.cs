using AEther.NAudio;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

namespace AEther.WinUI;

public sealed class MainWindow : Window
{
    readonly SwapChainPanel canvas = new();
    readonly UserControl root;
    readonly SettingsWindow settings = new();
    readonly ShaderWindow shaders = new();
    readonly SemaphoreSlim sessionGate = new(1, 1);
    readonly DispatcherTimer reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer timingTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly Func<SampleSource>? createSource;
    readonly string? shaderDirectory;
    Session? session;
    RenderWorker? renderWorker;
    FileSystemWatcher? watcher;
    bool loaded, closing, shutdownComplete, fullscreen;
    RectInt32 windowBounds;

    public MainWindow(Func<SampleSource>? createSource = null, string? shaderDirectory = null)
    {
        this.createSource = createSource;
        this.shaderDirectory = shaderDirectory;
        Title = "AEther";
        // A focusable host routes keyboard input without adding anything over the canvas.
        root = new UserControl { Content = canvas, IsTabStop = true, UseSystemFocusVisuals = false };
        Content = root;
        AppWindow.Resize(new SizeInt32(1280, 720));
        settings.SetOwner(this);
        shaders.SetOwner(this);
        shaders.SceneChanged += (_, _) =>
        {
            Title = $"AEther - {shaders.SelectedScene}";
            settings.SetScenes(session?.Renderer.Scenes.Select(scene => scene.Name) ?? [], shaders.SelectedScene);
        };
        settings.SettingsChanged += async (_, _) => await RestartAsync();
        settings.ShaderWindowRequested += (_, _) => shaders.Toggle();
        settings.SceneSelected += name =>
        {
            if (session?.Renderer.SceneName != name) shaders.SelectScene(name);
            shaders.Show();
        };
        settings.ShortcutRequested += HandleToolShortcut;
        shaders.ShortcutRequested += key =>
        {
            if (key == VirtualKey.F6) shaders.Hide();
            else HandleToolShortcut(key);
        };
        ToolWindow.AddShortcut(root, VirtualKey.Space, settings.Toggle);
        ToolWindow.AddShortcut(root, VirtualKey.F6, shaders.Toggle);
        ToolWindow.AddShortcut(root, VirtualKey.F11, ToggleFullscreen);
        ToolWindow.AddShortcut(root, VirtualKey.F5, () => _ = RestartAsync());
        ToolWindow.AddShortcut(root, VirtualKey.Escape, RequestClose);
        ToolWindow.AddShortcut(root, VirtualKey.R, () => { if (session != null) { session.Renderer.CombinedLevel ^= true; shaders.RefreshParameters(); } });
        ToolWindow.AddShortcut(root, VirtualKey.I, () => { if (session != null) { session.Renderer.Intensity ^= true; shaders.RefreshParameters(); } });
        ToolWindow.AddShortcut(root, (VirtualKey)219, () => ChangeSlice(-.05f));
        ToolWindow.AddShortcut(root, (VirtualKey)221, () => ChangeSlice(.05f));
        canvas.Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            root.Focus(FocusState.Programmatic);
            await RestartAsync();
        };
        Activated += (_, e) =>
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
            shaders.Reload();
            if (session.Renderer.ShaderError != null) shaders.Show();
        };
        // A Tick already queued before Stop can still arrive during window teardown.
        timingTimer.Tick += (_, _) => { if (!closing && settings.IsVisible) settings.SetTiming(session); };
        timingTimer.Start();
        CompositionTarget.Rendering += Render;
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

    async Task RestartAsync()
    {
        if (closing || !loaded) return;
        await sessionGate.WaitAsync();
        try
        {
            if (closing) return;
            await StopSessionAsync();
            if (closing) return;
            MoveToDisplay();
            SampleSource source = createSource?.Invoke() ?? (settings.InputName == SettingsWindow.Loopback
                ? new Loopback() : new Recorder(settings.InputName));
            Session? next = null;
            try
            {
                var size = CanvasSize;
                next = new(WinRT.Interop.WindowNative.GetWindowHandle(this), size.Width, size.Height,
                    source, settings.Options, shaderDirectory, chain => SwapChainHost.Attach(canvas, chain));
                next.Renderer.SetCompositionScale(canvas.CompositionScaleX, canvas.CompositionScaleY);
                next.CaptureStopped += CaptureStopped;
                next.Start();
                session = next;
                renderWorker = new(() => next.Renderer.Render(), error => DispatcherQueue.TryEnqueue(() => RenderFailed(error)));
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
                settings.SetStatus("Running. Space: settings · F6: shaders · F11: fullscreen\nF5: restart · R: level · I: intensity · [ / ]: slice");
            }
            catch
            {
                session = null;
                shaders.Attach(null);
                if (next != null) { try { await next.StopAsync(); } finally { next.Dispose(); } }
                else source.Dispose();
                throw;
            }
        }
        catch (Exception error) { settings.SetStatus(error.Message); settings.Show(); }
        finally { sessionGate.Release(); }
    }

    void CaptureStopped(object? sender, Exception? error)
    {
        if (error == null || closing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !ReferenceEquals(sender, session)) return;
            settings.SetStatus(error.Message);
            settings.Show();
        });
    }

    void ShaderChanged(object sender, FileSystemEventArgs e)
    {
        if (closing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !ReferenceEquals(sender, watcher)) return;
            reloadTimer.Stop(); reloadTimer.Start();
        });
    }

    async Task StopSessionAsync()
    {
        reloadTimer.Stop();
        watcher?.Dispose(); watcher = null;
        var previous = session; session = null;
        renderWorker?.Dispose(); renderWorker = null;
        shaders.Attach(null);
        if (previous == null) return;
        previous.CaptureStopped -= CaptureStopped;
        try { await previous.StopAsync(); }
        finally { previous.Dispose(); }
    }

    void MoveToDisplay()
    {
        var display = settings.SelectedDisplay;
        if (display == null) return;
        var current = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        if (fullscreen)
        {
            if (current.DisplayId.Value == display.DisplayId.Value) return;
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            AppWindow.Move(new PointInt32(display.WorkArea.X + 40, display.WorkArea.Y + 40));
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else if (current.DisplayId.Value != display.DisplayId.Value)
            AppWindow.Move(new PointInt32(display.WorkArea.X + 40, display.WorkArea.Y + 40));
    }

    void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            windowBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            MoveToDisplay();
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            fullscreen = true;
        }
        else
        {
            fullscreen = false;
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            AppWindow.MoveAndResize(windowBounds);
        }
    }

    void Render(object? sender, object e)
    {
        if (closing || session == null || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        renderWorker?.RequestFrame();
    }

    void RenderFailed(Exception error)
    {
        settings.SetStatus(error.Message);
        settings.Show();
        _ = StopAfterRenderFailureAsync();
    }

    async Task StopAfterRenderFailureAsync()
    {
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { if (!closing) settings.SetStatus(error.Message); }
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
        CompositionTarget.Rendering -= Render;
        timingTimer.Stop();
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        finally
        {
            sessionGate.Release();
            shaders.Destroy();
            settings.Destroy();
            shutdownComplete = true;
            // Closing can finish synchronously; do not reenter WinUI's Closing event.
            DispatcherQueue.TryEnqueue(() => base.Close());
        }
    }
}

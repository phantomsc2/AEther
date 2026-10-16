using AEther.NAudio;
using System.Runtime.InteropServices;

namespace AEther.WindowsForms;

public sealed class MainForm : Form
{
    readonly Panel canvas = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    readonly SettingsForm settings = new();
    readonly ShaderForm shaders = new();
    readonly SemaphoreSlim sessionGate = new(1, 1);
    readonly System.Windows.Forms.Timer reloadTimer = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer timingTimer = new() { Interval = 500 };
    readonly Func<SampleSource>? createSource;
    readonly string? shaderDirectory;
    Session? session;
    FileSystemWatcher? watcher;
    bool closing, shutdownComplete, fullscreen;
    Rectangle windowBounds;

    public MainForm(Func<SampleSource>? createSource = null, string? shaderDirectory = null)
    {
        this.createSource = createSource; this.shaderDirectory = shaderDirectory;
        Text = "AEther"; ClientSize = new(1264, 681); KeyPreview = true;
        Controls.Add(canvas);
        AddOwnedForm(settings); AddOwnedForm(shaders);
        shaders.SceneChanged += (_, _) =>
        {
            Text = $"AEther - {shaders.SelectedScene}";
            settings.SetScenes(session?.Renderer.Scenes.Select(scene => scene.Name) ?? [], shaders.SelectedScene);
        };
        shaders.ShortcutRequested += key =>
        {
            if (key == Keys.F11) ToggleFullscreen();
            if (key == Keys.F5) _ = RestartAsync();
        };
        settings.SettingsChanged += async (_, _) => await RestartAsync();
        settings.ShaderWindowRequested += (_, _) => ToggleShaderWindow();
        settings.SceneSelected += name =>
        {
            if (session?.Renderer.SceneName != name) shaders.SelectScene(name);
            ShowShaderWindow();
        };
        settings.ShortcutRequested += key =>
        {
            if (key == Keys.F11) ToggleFullscreen();
            if (key == Keys.F5) _ = RestartAsync();
        };
        canvas.Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized)
                session?.Renderer.Resize(canvas.ClientSize.Width, canvas.ClientSize.Height);
        };
        reloadTimer.Tick += (_, _) =>
        {
            reloadTimer.Stop();
            if (session is { } active && !active.Renderer.ReloadShaders())
            { shaders.SetStatus(active.Renderer.ShaderError!); ShowShaderWindow(); }
            else shaders.SetStatus("Shaders reloaded.");
        };
        Application.Idle += OnIdle;
        timingTimer.Tick += (_, _) => { if (settings.Visible) settings.SetTiming(session); };
        timingTimer.Start();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RestartAsync();
    }

    async Task RestartAsync()
    {
        if (closing || !IsHandleCreated) return;
        await sessionGate.WaitAsync();
        try
        {
            if (closing) return;
            await StopSessionAsync();
            if (closing) return;
            MoveToDisplay();
            SampleSource source = createSource?.Invoke() ?? (settings.InputName == SettingsForm.Loopback
                ? new Loopback() : new Recorder(settings.InputName));
            Session? next = null;
            try
            {
                next = new(canvas.Handle, canvas.ClientSize.Width, canvas.ClientSize.Height, source, settings.Options, shaderDirectory);

                next.CaptureStopped += CaptureStopped;
                next.Start();
                session = next;
                shaders.Attach(next.Renderer);
                Text = $"AEther - {shaders.SelectedScene}";
                watcher = new(next.Renderer.ShaderDirectory, "*.hlsl") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName };
                watcher.Changed += ShaderChanged; watcher.Created += ShaderChanged; watcher.Renamed += ShaderChanged; watcher.Deleted += ShaderChanged;
                watcher.EnableRaisingEvents = true;
                settings.SetStatus("Running. F6: shaders | Space: settings · F11: fullscreen\nF5: restart · R: level · I: intensity · [ / ]: slice");
            }
            catch
            {
                session = null;
                if (next != null) { try { await next.StopAsync(); } finally { next.Dispose(); } }
                else source.Dispose();
                throw;
            }
        }
        catch (Exception error) { settings.SetStatus(error.Message); settings.Show(this); }
        finally { sessionGate.Release(); }
    }

    void CaptureStopped(object? sender, Exception? error)
    {
        if (error == null || closing) return;
        try { BeginInvoke(() => { if (closing) return; settings.SetStatus(error.Message); settings.Show(this); }); }
        catch (InvalidOperationException) when (closing || IsDisposed) { }
    }

    void ShaderChanged(object sender, FileSystemEventArgs e)
    {
        if (closing) return;
        try { BeginInvoke(() => { if (closing) return; reloadTimer.Stop(); reloadTimer.Start(); }); }
        catch (InvalidOperationException) when (closing || IsDisposed) { }
    }

    async Task StopSessionAsync()
    {
        reloadTimer.Stop(); watcher?.Dispose(); watcher = null;
        var previous = session; session = null; shaders.Attach(null);
        if (previous == null) return;
        previous.CaptureStopped -= CaptureStopped;
        try { await previous.StopAsync(); } finally { previous.Dispose(); }
    }

    void MoveToDisplay()
    {
        var screen = Screen.AllScreens[Math.Min(settings.DisplayIndex, Screen.AllScreens.Length - 1)];
        if (fullscreen) Bounds = screen.Bounds;
        else if (Screen.FromControl(this).DeviceName != screen.DeviceName)
            Location = new(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40);
    }

    void ToggleFullscreen()
    {
        if (!fullscreen)
        {
            windowBounds = Bounds; WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None; fullscreen = true; MoveToDisplay();
        }
        else
        {
            fullscreen = false; FormBorderStyle = FormBorderStyle.Sizable; Bounds = windowBounds;
        }
    }

    void ToggleShaderWindow()
    {
        if (shaders.Visible) shaders.Hide(); else ShowShaderWindow();
    }

    void ShowShaderWindow()
    {
        if (shaders.Visible) shaders.Activate(); else shaders.Show(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Space:
                if (settings.Visible) settings.Hide(); else settings.Show(this);
                return true;
            case Keys.F6: ToggleShaderWindow(); return true;
            case Keys.F11: ToggleFullscreen(); return true;
            case Keys.F5: _ = RestartAsync(); return true;
            case Keys.Escape: Close(); return true;
            case Keys.R: if (session != null) session.Renderer.CombinedLevel ^= true; return true;
            case Keys.I: if (session != null) session.Renderer.Intensity ^= true; return true;
            case Keys.OemOpenBrackets: if (session != null) session.Renderer.SliceDepth -= .05f; return true;
            case Keys.OemCloseBrackets: if (session != null) session.Renderer.SliceDepth += .05f; return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void OnIdle(object? sender, EventArgs e)
    {
        while (!closing && session != null && !PeekMessage(out _, IntPtr.Zero, 0, 0, 0))
        {
            if (WindowState == FormWindowState.Minimized) break;
            try { session.Renderer.Render(); }
            catch (Exception error)
            {
                settings.SetStatus(error.Message); settings.Show(this);
                _ = StopAfterRenderFailureAsync(); break;
            }
        }
    }

    async Task StopAfterRenderFailureAsync()
    {
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { if (!closing) settings.SetStatus(error.Message); }
        finally { sessionGate.Release(); }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (shutdownComplete) { base.OnFormClosing(e); return; }
        e.Cancel = true; base.OnFormClosing(e);
        if (closing) return;
        closing = true; Application.Idle -= OnIdle;
        await sessionGate.WaitAsync();
        try { await StopSessionAsync(); }
        catch (Exception error) { System.Diagnostics.Debug.WriteLine(error); }
        finally
        {
            sessionGate.Release(); reloadTimer.Dispose(); timingTimer.Dispose(); settings.Dispose(); shaders.Dispose();
            shutdownComplete = true; Close();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeMessage
    {
        public IntPtr Window; public uint Message; public UIntPtr WParam; public IntPtr LParam;
        public uint Time; public Point Position; public uint Private;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
}

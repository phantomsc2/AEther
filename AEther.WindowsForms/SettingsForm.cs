using AEther.NAudio;
using AEther.Vortice;

namespace AEther.WindowsForms;

public sealed class SettingsForm : Form
{
    public const string Loopback = "System audio (loopback)";
    readonly ComboBox input = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox scene = new() { Name = "Scene", Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox display = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    readonly PropertyGrid properties = new() { Dock = DockStyle.Fill, ToolbarVisible = false };
    readonly Label timing = new() { Dock = DockStyle.Fill, AutoSize = false, Padding = new(0, 6, 0, 0) };
    readonly PropertyGrid mandelboxProperties = new() { Name = "MandelboxProperties", Dock = DockStyle.Fill, ToolbarVisible = false, PropertySort = PropertySort.NoSort };
    public MandelboxOptions Mandelbox { get; } = new();
    public AnalyzerOptions Options { get; } = new();
    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public Scene Scene => (Scene)(scene.SelectedItem ?? Scene.Spectrum);
    public int DisplayIndex => Math.Max(0, display.SelectedIndex);
    public event EventHandler? SettingsChanged;
    public event EventHandler? RenderingChanged;
    public event Action<Keys>? ShortcutRequested;

    public SettingsForm()
    {
        Text = "AEther settings"; ClientSize = new(390, 820); MinimumSize = new(330, 520);
        ShowInTaskbar = false; KeyPreview = true; StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 11, Padding = new(12) };
        for (var i = 0; i < 6; i++) layout.RowStyles.Add(new(SizeType.Absolute, i % 2 == 0 ? 22 : 32));
        layout.RowStyles.Add(new(SizeType.Absolute, 22));
        layout.RowStyles.Add(new(SizeType.Absolute, 285));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 205));
        layout.RowStyles.Add(new(SizeType.Absolute, 80));
        layout.Controls.Add(new Label { Text = "Audio input", AutoSize = true }, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(new Label { Text = "Scene", AutoSize = true }, 0, 2);
        layout.Controls.Add(scene, 0, 3);
        layout.Controls.Add(new Label { Text = "Display", AutoSize = true }, 0, 4);
        layout.Controls.Add(display, 0, 5);
        layout.Controls.Add(new Label { Text = "Mandelbox", AutoSize = true }, 0, 6);
        layout.Controls.Add(mandelboxProperties, 0, 7);
        layout.Controls.Add(properties, 0, 8); layout.Controls.Add(timing, 0, 9); layout.Controls.Add(status, 0, 10);
        SetTiming(null);
        Controls.Add(layout);
        input.Items.Add(Loopback);
        input.Items.AddRange(Recorder.GetAvailableDeviceNames());
        input.SelectedIndex = 0;
        scene.DataSource = Enum.GetValues<Scene>(); scene.SelectedItem = Scene.Spectrum;
        foreach (var screen in Screen.AllScreens)
            display.Items.Add($"{screen.DeviceName} ({screen.Bounds.Width} × {screen.Bounds.Height})");
        display.SelectedIndex = Array.FindIndex(Screen.AllScreens, s => s.Primary);
        properties.SelectedObject = Options;
        mandelboxProperties.SelectedObject = Mandelbox;
        Mandelbox.Changed += (_, _) => RenderingChanged?.Invoke(this, EventArgs.Empty);
        input.SelectedIndexChanged += Changed;
        scene.SelectedIndexChanged += Changed;
        display.SelectedIndexChanged += Changed;
        properties.PropertyValueChanged += (_, _) => Changed(this, EventArgs.Empty);
        SetStatus("Space: settings · F11: fullscreen · F5: restart\nR: level view · I: spectrum intensity · [ / ]: slice");
    }

    void Changed(object? sender, EventArgs e) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    public void SetStatus(string text) => status.Text = text;
    public void SetTiming(Session? session)
    {
        if (session == null) { timing.Text = "Timing\nNo active audio session."; return; }
        var analyzer = session.Analyzer;
        var profile = analyzer.Profile;
        var format = analyzer.Format;
        var audio = session.Renderer.Audio;
        string Stat(string stage)
        {
            var value = profile.Recent(stage);
            return value is { } v ? $"{v.Mean:F2} / {v.P95:F2} ms" : "waiting";
        }
        var interval = profile.Recent("render.frame_interval_ms");
        var fps = interval is { Mean: > 0 } frame ? $"{1000 / frame.Mean:F1} FPS" : "waiting";
        var rate = format.SampleRate / (double)(format.SampleRate / analyzer.Options.TimeResolution);
        timing.Text = $"Timing (mean / p95, last 256 measurements)\n"
            + $"Audio: {format.SampleRate:N0} Hz, {format.ChannelCount} channels\n"
            + $"Analysis: {rate:F1} Hz ({1000 / rate:F2} ms/block)\n"
            + $"Capture to draw: {Stat("render.capture_to_submission_ms")}\n"
            + $"Capture queue: {Stat("capture.queue_ms")}\n"
            + $"Analysis CPU: {Stat("analysis.total_ms")}\n"
            + $"Draw CPU: {Stat("render.draw_cpu_ms")}\n"
            + $"Present wait: {Stat("render.present_wait_ms")} | {fps}\n"
            + $"History delay: {audio.PresentationDelay * 1000:F0} ms | Drops: {analyzer.DroppedInputCount} input, {audio.DroppedRows} visual\n"
            + "Capture to draw excludes device buffering and display scanout.";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Space or Keys.Escape) { Hide(); return true; }
        if (keyData is Keys.F11 or Keys.F5) { ShortcutRequested?.Invoke(keyData); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

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
    public AnalyzerOptions Options { get; } = new();
    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public Scene Scene => (Scene)(scene.SelectedItem ?? Scene.Spectrum);
    public int DisplayIndex => Math.Max(0, display.SelectedIndex);
    public event EventHandler? SettingsChanged;
    public event Action<Keys>? ShortcutRequested;

    public SettingsForm()
    {
        Text = "AEther settings"; ClientSize = new(390, 720); MinimumSize = new(330, 400);
        ShowInTaskbar = false; KeyPreview = true; StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new(12) };
        for (var i = 0; i < 6; i++) layout.RowStyles.Add(new(SizeType.Absolute, i % 2 == 0 ? 22 : 32));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 80));
        layout.Controls.Add(new Label { Text = "Audio input", AutoSize = true }, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(new Label { Text = "Scene", AutoSize = true }, 0, 2);
        layout.Controls.Add(scene, 0, 3);
        layout.Controls.Add(new Label { Text = "Display", AutoSize = true }, 0, 4);
        layout.Controls.Add(display, 0, 5);
        layout.Controls.Add(properties, 0, 6); layout.Controls.Add(status, 0, 7);
        Controls.Add(layout);
        input.Items.Add(Loopback);
        input.Items.AddRange(Recorder.GetAvailableDeviceNames());
        input.SelectedIndex = 0;
        scene.DataSource = Enum.GetValues<Scene>(); scene.SelectedItem = Scene.Spectrum;
        foreach (var screen in Screen.AllScreens)
            display.Items.Add($"{screen.DeviceName} ({screen.Bounds.Width} × {screen.Bounds.Height})");
        display.SelectedIndex = Array.FindIndex(Screen.AllScreens, s => s.Primary);
        properties.SelectedObject = Options;
        input.SelectedIndexChanged += Changed;
        scene.SelectedIndexChanged += Changed;
        display.SelectedIndexChanged += Changed;
        properties.PropertyValueChanged += (_, _) => Changed(this, EventArgs.Empty);
        SetStatus("Space: settings · F11: fullscreen · F5: restart\nR: level view · I: spectrum intensity · [ / ]: slice");
    }

    void Changed(object? sender, EventArgs e) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    public void SetStatus(string text) => status.Text = text;

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

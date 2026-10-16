using AEther.Vortice;

namespace AEther.WindowsForms;

public sealed class ShaderForm : Form
{
    readonly FlowLayoutPanel parameters = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new(8) };
    readonly Label status = new() { Dock = DockStyle.Bottom, Height = 90, AutoEllipsis = true };
    Renderer? renderer;
    readonly Dictionary<string, ShaderParameters> saved = new(StringComparer.Ordinal);
    public string SelectedScene { get; private set; } = "Mandelbox";
    public event EventHandler? SceneChanged;
    public event Action<Keys>? ShortcutRequested;

    public ShaderForm()
    {
        Text = "AEther shaders";
        ClientSize = new(460, 760); MinimumSize = new(400, 400);
        ShowInTaskbar = false; KeyPreview = true; StartPosition = FormStartPosition.CenterParent;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38 };
        var reload = new Button { Text = "Reload", AutoSize = true };
        var reset = new Button { Text = "Reset defaults", AutoSize = true };
        buttons.Controls.AddRange([reload, reset]);
        reload.Click += (_, _) => Reload();
        reset.Click += (_, _) => { renderer?.ActiveShader?.Parameters?.Reset(); RefreshParameters(); };
        Controls.Add(parameters); Controls.Add(status); Controls.Add(buttons);
    }

    public void Attach(Renderer? value)
    {
        if (renderer != null)
        {
            renderer.ShadersChanged -= ShadersChanged;
            foreach (var scene in renderer.Scenes)
                if (scene.Parameters != null) saved[scene.Name] = scene.Parameters;
        }
        renderer = value;
        if (renderer != null)
        {
            foreach (var scene in renderer.Scenes)
                if (scene.Parameters != null && saved.TryGetValue(scene.Name, out var previous)) scene.Parameters.Restore(previous);
            if (renderer.Scenes.Any(s => s.Name == SelectedScene)) renderer.SelectScene(SelectedScene);
            renderer.ShadersChanged += ShadersChanged;
        }
        RefreshScene();
    }

    public void SelectScene(string name)
    {
        if (renderer == null) return;
        renderer.SelectScene(name);
        RefreshScene();
    }

    void ShadersChanged(object? sender, EventArgs e) => RefreshScene();
    public void Reload()
    {
        if (renderer == null) return;
        var success = renderer.ReloadShaders();
        SetStatus(success ? "Shaders reloaded." : renderer.ShaderError!);
    }
    public void SetStatus(string message) => status.Text = message;

    void RefreshScene()
    {
        if (renderer != null)
        {
            SelectedScene = renderer.SceneName;
            status.Text = renderer.ShaderDirectory;
        }
        else status.Text = "No active rendering session.";
        Text = $"AEther shaders - {SelectedScene}";
        RefreshParameters();
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    void RefreshParameters()
    {
        parameters.SuspendLayout();
        try
        {
            foreach (Control control in parameters.Controls.Cast<Control>().ToArray()) control.Dispose();
            parameters.Controls.Clear();
            if (renderer?.ActiveShader?.Parameters is not { } values)
            {
                parameters.Controls.Add(new Label { Text = "This shader has no editable parameters.", AutoSize = true });
                return;
            }
            foreach (var parameter in values.Items) parameters.Controls.Add(Editor(values, parameter, 390));
        }
        finally { parameters.ResumeLayout(true); }
    }

    static Control Editor(ShaderParameters values, ShaderParameter parameter, int width)
    {
        if (parameter.Children.Count != 0)
        {
            var children = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Location = new(8, 22) };
            foreach (var child in parameter.Children) children.Controls.Add(Editor(values, child, width - 24));
            var group = new GroupBox { Text = parameter.Name, Width = width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(8, 22, 8, 8) };
            group.Controls.Add(children);
            return group;
        }
        if (parameter.IsBoolean)
        {
            var checkbox = new CheckBox { Text = parameter.Name, Checked = values.GetBoolean(parameter), AutoSize = true, Tag = parameter.Path };
            checkbox.CheckedChanged += (_, _) => values.SetBoolean(parameter, checkbox.Checked);
            return checkbox;
        }
        var row = new Panel { Width = width, Height = 65, Tag = parameter.Path };
        var label = new Label { Text = parameter.Name, AutoSize = true, Location = new(0, 2) };
        var number = new NumericUpDown { Minimum = 0, Maximum = 1, DecimalPlaces = 4, Increment = .01m, Value = (decimal)values.GetFloat(parameter), Width = 82, Location = new(width - 86, 0) };
        var slider = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Width = width, Height = 32, Location = new(0, 27), Value = (int)Math.Round(values.GetFloat(parameter) * 1000) };
        bool updating = false;
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            updating = true;
            try { number.Value = slider.Value / 1000m; values.SetFloat(parameter, slider.Value / 1000f); }
            finally { updating = false; }
        };
        number.ValueChanged += (_, _) =>
        {
            if (updating) return;
            updating = true;
            try { slider.Value = (int)Math.Round(number.Value * 1000); values.SetFloat(parameter, (float)number.Value); }
            finally { updating = false; }
        };
        row.Controls.AddRange([label, number, slider]);
        return row;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is Keys.Escape or Keys.F6) { Hide(); return true; }
        if (keyData is Keys.F11 or Keys.F5) { ShortcutRequested?.Invoke(keyData); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && renderer != null) renderer.ShadersChanged -= ShadersChanged;
        base.Dispose(disposing);
    }
}

using AEther.Vortice;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace AEther.WinUI;

public sealed class ShaderWindow : ToolWindow
{
    readonly StackPanel parameters = new() { Spacing = 12 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly Dictionary<string, ShaderParameters> saved = new(StringComparer.Ordinal);
    Renderer? renderer;
    // Allows restarting a preview after renderer changes without losing its scene.
    public string SelectedScene { get; private set; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(argument => argument.StartsWith("--scene=", StringComparison.Ordinal))?[8..] ?? "Mandelbox";
    public event EventHandler? SceneChanged;

    public ShaderWindow() : base("AEther scene", 520, 800)
    {
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var reload = new Button { Content = "Reload" };
        var reset = new Button { Content = "Reset defaults" };
        buttons.Children.Add(reload); buttons.Children.Add(reset);
        reload.Click += (_, _) => Reload();
        reset.Click += (_, _) => { renderer?.ActiveShader?.Parameters?.Reset(); RefreshParameters(); };
        var scroll = new ScrollViewer { Content = parameters, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); Grid.SetRow(status, 2);
        Layout.Children.Add(buttons); Layout.Children.Add(scroll); Layout.Children.Add(status);
        Closed += (_, _) => { if (renderer != null) renderer.ShadersChanged -= ShadersChanged; };
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
        bool firstCompilation = renderer.Scenes.Single(scene => scene.Name == name).Parameters == null;
        renderer.SelectScene(name);
        if (firstCompilation && saved.TryGetValue(name, out var previous))
            renderer.ActiveShader?.Parameters?.Restore(previous);
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
            // Retain visited scenes even when a reload leaves them uncompiled.
            if (renderer.ActiveShader?.Parameters is { } values) saved[SelectedScene] = values;
            status.Text = renderer.ShaderDirectory;
        }
        else status.Text = "No active rendering session.";
        Title = $"AEther scene - {SelectedScene}";
        RefreshParameters();
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshParameters()
    {
        parameters.Children.Clear();
        if (renderer?.ActiveShader?.Parameters is not { } values)
        {
            parameters.Children.Add(new TextBlock { Text = "This shader has no editable parameters.", TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var parameter in values.Items) parameters.Children.Add(Editor(values, parameter));
    }

    static UIElement Editor(ShaderParameters values, ShaderParameter parameter)
    {
        if (parameter.Children.Count != 0)
        {
            var children = new StackPanel { Spacing = 12 };
            foreach (var child in parameter.Children) children.Children.Add(Editor(values, child));
            return new Expander
            {
                Header = parameter.Name, Content = children, IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }
        if (parameter.IsBoolean)
        {
            var checkbox = new CheckBox { Content = parameter.Name, IsChecked = values.GetBoolean(parameter), Tag = parameter.Path };
            checkbox.Checked += (_, _) => values.SetBoolean(parameter, true);
            checkbox.Unchecked += (_, _) => values.SetBoolean(parameter, false);
            return checkbox;
        }
        var row = new Grid { RowSpacing = 4, ColumnSpacing = 12, Tag = parameter.Path };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new TextBlock { Text = parameter.Name, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var number = new NumberBox
        {
            Minimum = 0, Maximum = 1, SmallChange = .01, LargeChange = .1,
            Value = values.GetFloat(parameter), Width = 140,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            NumberFormatter = new DecimalFormatter { FractionDigits = 4 },
        };
        var slider = new Slider { Minimum = 0, Maximum = 1, StepFrequency = .001, Value = values.GetFloat(parameter) };
        AutomationProperties.SetName(number, parameter.Name);
        AutomationProperties.SetName(slider, parameter.Name);
        bool updating = false;
        SliderCommit.Bind(slider, value =>
        {
            if (updating) return;
            updating = true;
            try { number.Value = value; }
            finally { updating = false; }
        }, value => { if (!updating) values.SetFloat(parameter, (float)value); });
        number.ValueChanged += (_, e) =>
        {
            if (updating || !double.IsFinite(e.NewValue)) return;
            updating = true;
            try { slider.Value = e.NewValue; values.SetFloat(parameter, (float)e.NewValue); }
            finally { updating = false; }
        };
        Grid.SetColumn(number, 1);
        Grid.SetRow(slider, 1); Grid.SetColumnSpan(slider, 2);
        row.Children.Add(label); row.Children.Add(number); row.Children.Add(slider);
        return row;
    }
}

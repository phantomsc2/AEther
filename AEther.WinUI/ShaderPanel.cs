using AEther.Vortice;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace AEther.WinUI;

public sealed class ShaderPanel : UserControl
{
    readonly Grid layout = new() { RowSpacing = 8 };
    readonly StackPanel parameters = new() { Spacing = 4 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
    readonly Dictionary<string, ShaderParameters> saved = new(StringComparer.Ordinal);
    Renderer? renderer;
    // Allows restarting a preview after renderer changes without losing its scene.
    public string SelectedScene { get; private set; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(argument => argument.StartsWith("--scene=", StringComparison.Ordinal))?[8..] ?? "Mandelbox";
    public event EventHandler? SceneChanged;

    public ShaderPanel()
    {
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = layout;
        var scroll = new ScrollViewer { Content = parameters, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(status, 1);
        layout.Children.Add(scroll); layout.Children.Add(status);
    }

    public void Attach(Renderer? value)
    {
        if (renderer != null)
        {
            renderer.ShadersChanged -= ShadersChanged;
            foreach (var scene in renderer.Scenes)
                if (renderer.GetSceneParameters(scene.Name) is { } parameters) saved[scene.Name] = parameters;
        }
        renderer = value;
        if (renderer != null)
        {
            foreach (var scene in renderer.Scenes)
                if (saved.TryGetValue(scene.Name, out var previous)) renderer.RestoreSceneParameters(scene.Name, previous);
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

    public void SetStatus(string message)
    {
        status.Text = message;
        status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    void RefreshScene()
    {
        if (renderer != null)
        {
            SelectedScene = renderer.SceneName;
            // Retain visited scenes even when a reload leaves them uncompiled.
            if (renderer.ActiveShader?.Parameters is { } values) saved[SelectedScene] = values;
            SetStatus(renderer.ShaderError ?? "");
        }
        else SetStatus("No active rendering session.");
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
            if (parameter.Name == "Display" && parameter.Children.Count == 1
                && parameter.Children[0] is { Name: "Exposure", Children.Count: 0 } exposure)
                return Editor(values, exposure);
            var children = new StackPanel { Spacing = 8 };
            foreach (var child in parameter.Children) children.Children.Add(Editor(values, child));
            return new Expander
            {
                Header = parameter.Name, Content = children, IsExpanded = false,
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
        var number = new NumberBox
        {
            Minimum = 0, Maximum = 1, SmallChange = .01, LargeChange = .1,
            Value = values.GetFloat(parameter),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            NumberFormatter = new DecimalFormatter { FractionDigits = 4 },
        };
        var slider = new Slider { Minimum = 0, Maximum = 1, StepFrequency = .001, Value = values.GetFloat(parameter) };
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
        var row = NumericEditor.Create(parameter.Name, number, slider);
        row.Tag = parameter.Path;
        return row;
    }
}

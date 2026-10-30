using AEther.Vortice;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

public sealed class ShaderPanel : UserControl
{
    readonly Grid layout = new() { RowSpacing = 8 };
    readonly StackPanel parameters = new() { Spacing = 4 };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
    readonly Dictionary<string, IReadOnlyDictionary<string, EffectSettings>> saved = new(StringComparer.Ordinal);
    Renderer? renderer;
    // Allows restarting a preview after renderer changes without losing its scene.
    public string? SelectedScene { get; private set; } = Environment.GetCommandLineArgs()
        .FirstOrDefault(argument => argument.StartsWith("--scene=", StringComparison.Ordinal))?[8..];
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
                if (renderer.GetSceneSettings(scene.Name) is { } parameters) saved[scene.Name] = parameters;
        }
        renderer = value;
        if (renderer != null)
        {
            foreach (var scene in renderer.Scenes)
                if (saved.TryGetValue(scene.Name, out var previous)) renderer.RestoreSceneSettings(scene.Name, previous);
            if (renderer.Scenes.Any(s => s.Name == SelectedScene)) renderer.SelectScene(SelectedScene);
            renderer.ShadersChanged += ShadersChanged;
        }
        RefreshScene();
    }

    public void SelectScene(string? name)
    {
        SelectedScene = name;
        if (renderer == null) return;
        if (name == null) { renderer.SelectScene(null); RefreshScene(); return; }
        bool firstCompilation = renderer.Scenes.Single(scene => scene.Name == name).Effects.Count == 0;
        renderer.SelectScene(name);
        if (firstCompilation && saved.TryGetValue(name, out var previous))
            renderer.ActiveShader?.RestoreSettings(previous);
        RefreshScene();
    }

    void ShadersChanged(object? sender, EventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) RefreshScene();
        else DispatcherQueue.TryEnqueue(RefreshScene);
    }

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
            if (SelectedScene != null && renderer.GetSceneSettings(SelectedScene) is { } values) saved[SelectedScene] = values;
            SetStatus(renderer.ShaderError ?? "");
        }
        else SetStatus("No active rendering session.");
        RefreshParameters();
        SceneChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshParameters()
    {
        parameters.Children.Clear();
        if (renderer?.ActiveShader is not { } scene) return;
        foreach (var effect in scene.Effects)
        {
            var group = new ShaderGroupLayout();
            void AddOptions(StackPanel content)
            {
                var ungrouped = new StackPanel { Spacing = 4 };
                while (content.Children.Count > 0)
                {
                    var child = content.Children[0];
                    content.Children.RemoveAt(0);
                    if (child is Expander expander)
                    {
                        expander.IsExpanded = true;
                        if (ungrouped.Children.Count > 0) { group.Children.Add(ungrouped); ungrouped = new StackPanel { Spacing = 4 }; }
                        group.Children.Add(child);
                    }
                    else ungrouped.Children.Add(child);
                }
                if (ungrouped.Children.Count > 0) group.Children.Add(ungrouped);
            }
            if (effect is Froxel4Effect tube)
            {
                var editor = new OptionsEditor<Froxel4Options>(tube.CloudOptions, v => v with { }, tube.DefaultCloudOptions);
                editor.Changed += (_, _) => tube.CloudOptions = editor.Options;
                AddOptions(editor.Content);
            }
            else if (effect is Froxel3Effect cloud)
            {
                var editor = new OptionsEditor<Froxel3Options>(cloud.CloudOptions, v => v with { }, cloud.DefaultCloudOptions);
                editor.Changed += (_, _) => cloud.CloudOptions = editor.Options;
                AddOptions(editor.Content);
            }
            else if (effect is Froxel2Effect ring)
            {
                var editor = new OptionsEditor<Froxel2Options>(ring.DepthOptions, v => v with { }, ring.DefaultDepthOptions);
                editor.Changed += (_, _) => ring.DepthOptions = editor.Options;
                group.Children.Add(editor.Content);
            }
            else if (effect is FroxelEffect froxel)
            {
                var editor = new OptionsEditor<FroxelOptions>(froxel.Options, v => v with { }, froxel.DefaultOptions);
                editor.Changed += (_, _) => froxel.Options = editor.Options;
                AddOptions(editor.Content);
            }
            if (effect is FluidEffect fluid)
            {
                var editor = new OptionsEditor<FluidOptions>(fluid.Options, v => v with { }, fluid.DefaultOptions);
                editor.Changed += (_, _) => fluid.Options = editor.Options;
                AddOptions(editor.Content);
            }
            if (effect is RasterEffect raster)
            {
                var editor = new OptionsEditor<RasterOptions>(raster.Options, v => v with { }, raster.DefaultOptions);
                editor.Changed += (_, _) => raster.Options = editor.Options;
                AddOptions(editor.Content);
            }
            if (effect.Parameters is { } values)
            {
                var ungrouped = new StackPanel { Spacing = 4 };
                foreach (var parameter in values.Items)
                {
                    if (parameter.Children.Count != 0)
                    {
                        if (ungrouped.Children.Count > 0) { group.Children.Add(ungrouped); ungrouped = new StackPanel { Spacing = 4 }; }
                        group.Children.Add(Editor(values, parameter));
                    }
                    else ungrouped.Children.Add(Editor(values, parameter));
                }
                if (ungrouped.Children.Count > 0) group.Children.Add(ungrouped);
            }
            foreach (var child in group.Children.OfType<FrameworkElement>())
                child.VerticalAlignment = VerticalAlignment.Top;
            parameters.Children.Add(new Expander
            {
                Header = effect.Name, Content = group, IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
        }
    }

    static UIElement Editor(ShaderParameters values, ShaderParameter parameter)
    {
        if (parameter.Children.Count != 0)
        {
            var children = new StackPanel { Spacing = 8 };
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
            ParameterMenu.Attach(checkbox, () =>
            {
                values.Reset(parameter);
                checkbox.IsChecked = values.GetBoolean(parameter);
            });
            return checkbox;
        }
        var number = new NumberBox
        {
            Minimum = 0, Maximum = 1, SmallChange = .01, LargeChange = .1,
            Value = values.GetFloat(parameter),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            NumberFormatter = NumericEditor.Formatter(),
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
        ParameterMenu.Attach(row, () =>
        {
            values.Reset(parameter);
            updating = true;
            try { number.Value = slider.Value = values.GetFloat(parameter); }
            finally { updating = false; }
        });
        return row;
    }
}

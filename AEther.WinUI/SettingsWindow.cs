using System.ComponentModel;
using AEther.NAudio;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Globalization.NumberFormatting;

namespace AEther.WinUI;

public sealed class SettingsWindow : ToolWindow
{
    public const string Loopback = "System audio (loopback)";
    readonly ComboBox input = new() { Header = "Audio input", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox display = new() { Header = "Display", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox scene = new() { Header = "Scene", Name = "Scene", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly TextBlock timing = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly IReadOnlyList<DisplayArea> displays;
    bool refreshingScenes;
    public AnalyzerOptions Options { get; } = new();
    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public DisplayArea? SelectedDisplay => display.SelectedIndex >= 0 && display.SelectedIndex < displays.Count
        ? displays[display.SelectedIndex] : null;
    public event EventHandler? SettingsChanged;
    public event EventHandler? ShaderWindowRequested;
    public event Action<string>? SceneSelected;

    public SettingsWindow() : base("AEther settings", 480, 840)
    {
        // Some Windows App Runtime versions expose indexed access without IIterable.
        var areas = DisplayArea.FindAll();
        displays = Enumerable.Range(0, areas.Count).Select(i => areas[i]).ToArray();
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(input);
        content.Children.Add(display);
        content.Children.Add(scene);
        var shaderButton = new Button { Content = "Shader properties" };
        shaderButton.Click += (_, _) => ShaderWindowRequested?.Invoke(this, EventArgs.Empty);
        content.Children.Add(shaderButton);
        content.Children.Add(new TextBlock { Text = "Analyzer", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        content.Children.Add(BuildOptions());
        var timingGroup = new Expander { Header = "Timing", Content = timing, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(timingGroup);
        content.Children.Add(status);
        Layout.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        input.Items.Add(Loopback);
        try { foreach (var name in Recorder.GetAvailableDeviceNames()) input.Items.Add(name); }
        catch (Exception error) { SetStatus(error.Message); }
        input.SelectedIndex = 0;
        foreach (var area in displays)
            display.Items.Add($"Display {display.Items.Count + 1}{(area.IsPrimary ? " (primary)" : "")} · {area.OuterBounds.Width} × {area.OuterBounds.Height}");
        display.SelectedIndex = displays.ToList().FindIndex(area => area.IsPrimary);
        input.SelectionChanged += (_, _) => Changed();
        display.SelectionChanged += (_, _) => Changed();
        scene.SelectionChanged += (_, _) =>
        {
            if (!refreshingScenes && scene.SelectedItem is string name) SceneSelected?.Invoke(name);
        };
        SetTiming(null);
    }

    UIElement BuildOptions()
    {
        var groups = new StackPanel { Spacing = 8 };
        var refreshNumbers = new List<Action>();
        var backendEditors = new List<(string Name, Control Editor)>();
        void RefreshBackendEditors()
        {
            foreach (var (name, editor) in backendEditors)
                editor.IsEnabled = name == nameof(AnalyzerOptions.WindowFunction) ? !Options.UseSwift : Options.UseSwift;
        }
        bool refreshingNumbers = false;
        var properties = TypeDescriptor.GetProperties(Options).Cast<PropertyDescriptor>().Where(p => p.IsBrowsable && !p.IsReadOnly);
        foreach (var category in properties.GroupBy(p => p.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rows = new StackPanel { Spacing = 12 };
            foreach (var property in category)
            {
                FrameworkElement editor;
                if (property.PropertyType == typeof(bool))
                {
                    var toggle = new ToggleSwitch { Header = property.DisplayName, IsOn = (bool)property.GetValue(Options)! };
                    if (property.Name == nameof(AnalyzerOptions.UseSwift))
                    {
                        toggle.OnContent = "SWIFT";
                        toggle.OffContent = "DFT";
                    }
                    toggle.Toggled += (_, _) => { property.SetValue(Options, toggle.IsOn); RefreshBackendEditors(); Changed(); };
                    editor = toggle;
                }
                else if (property.PropertyType.IsEnum)
                {
                    var combo = new ComboBox { Header = property.DisplayName, HorizontalAlignment = HorizontalAlignment.Stretch };
                    var values = Enum.GetValues(property.PropertyType).Cast<object>();
                    foreach (var value in values)
                    {
                        var label = property.PropertyType.GetField(value.ToString()!)?
                            .GetCustomAttributes(typeof(DescriptionAttribute), false)
                            .OfType<DescriptionAttribute>().FirstOrDefault()?.Description ?? value.ToString();
                        var item = new ComboBoxItem { Content = label, Tag = value };
                        combo.Items.Add(item);
                        if (Equals(value, property.GetValue(Options))) combo.SelectedItem = item;
                    }
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is not ComboBoxItem item) return;
                        property.SetValue(Options, item.Tag); Changed();
                    };
                    editor = combo;
                }
                else
                {
                    var integral = property.PropertyType == typeof(int);
                    var logarithmic = property.Name == nameof(AnalyzerOptions.SwiftWidthScale);
                    var step = integral ? 1 : property.Name is nameof(AnalyzerOptions.SwiftWidthScale)
                        or nameof(AnalyzerOptions.SwiftImbalance) ? .01 : .001;
                    double ToSlider(double value) => logarithmic ? Math.Log2(value) : value;
                    double FromSlider(double value) => logarithmic ? Math.Pow(2, value) : value;
                    var number = new NumberBox
                    {
                        Header = property.DisplayName,
                        Value = Convert.ToDouble(property.GetValue(Options)),
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                        SmallChange = step,
                        LargeChange = integral ? 10 : .1,
                        NumberFormatter = new DecimalFormatter { FractionDigits = integral ? 0 : 3 },
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Minimum = NumericBounds(property.Name).Minimum,
                        Maximum = NumericBounds(property.Name).Maximum,
                    };
                    var slider = new Slider
                    {
                        Minimum = ToSlider(number.Minimum), Maximum = ToSlider(number.Maximum),
                        StepFrequency = logarithmic ? .05 : step,
                        SmallChange = logarithmic ? .05 : number.SmallChange,
                        LargeChange = logarithmic ? .5 : number.LargeChange,
                        Value = ToSlider(number.Value),
                    };
                    if (logarithmic) slider.ThumbToolTipValueConverter = new WidthToolTipConverter();
                    AutomationProperties.SetName(number, property.DisplayName);
                    AutomationProperties.SetName(slider, property.DisplayName);
                    bool updating = false;
                    refreshNumbers.Add(() =>
                    {
                        var (min, max) = NumericBounds(property.Name);
                        number.Minimum = Math.Min(number.Minimum, min);
                        slider.Minimum = ToSlider(number.Minimum);
                        number.Maximum = Math.Max(number.Maximum, max);
                        slider.Maximum = ToSlider(number.Maximum);
                        number.Maximum = max; slider.Maximum = ToSlider(max);
                        number.Minimum = min; slider.Minimum = ToSlider(min);
                        var value = Math.Clamp(Convert.ToDouble(property.GetValue(Options)), min, max);
                        property.SetValue(Options, Convert.ChangeType(value, property.PropertyType));
                        number.Value = value; slider.Value = ToSlider(value);
                    });
                    slider.ValueChanged += (_, e) =>
                    {
                        if (!updating && !refreshingNumbers) number.Value = FromSlider(e.NewValue);
                    };
                    number.ValueChanged += (_, e) =>
                    {
                        if (updating || refreshingNumbers || !double.IsFinite(e.NewValue)) return;
                        var value = integral ? Math.Round(e.NewValue) : e.NewValue;
                        updating = true;
                        try
                        {
                            number.Value = value; slider.Value = ToSlider(value);
                            property.SetValue(Options, Convert.ChangeType(value, property.PropertyType));
                        }
                        finally { updating = false; }
                        refreshingNumbers = true;
                        try { foreach (var refresh in refreshNumbers) refresh(); }
                        finally { refreshingNumbers = false; }
                        Changed();
                    };
                    var row = new StackPanel { Spacing = 4 };
                    row.Children.Add(number);
                    row.Children.Add(slider);
                    editor = row;
                }
                if (property.Name is nameof(AnalyzerOptions.WindowFunction) or nameof(AnalyzerOptions.SwiftWidthScale)
                    or nameof(AnalyzerOptions.SwiftImbalance))
                {
                    var container = new ContentControl { Content = editor, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    backendEditors.Add((property.Name, container));
                    editor = container;
                }
                AutomationProperties.SetName(editor, property.DisplayName);
                if (!string.IsNullOrWhiteSpace(property.Description)) ToolTipService.SetToolTip(editor, property.Description);
                rows.Children.Add(editor);
            }
            groups.Children.Add(new Expander
            {
                Header = category.Key, Content = rows, IsExpanded = true,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
        }
        RefreshBackendEditors();
        return groups;
    }

    (double Minimum, double Maximum) NumericBounds(string name) => name switch
    {
        nameof(AnalyzerOptions.BufferCapacity) => (1, 64),
        nameof(AnalyzerOptions.MaxInputAgeMilliseconds) => (1, 1000),
        nameof(AnalyzerOptions.MinFrequency) => (1, Options.MaxFrequency - 1),
        nameof(AnalyzerOptions.MaxFrequency) => (Options.MinFrequency + 1, 22050),
        nameof(AnalyzerOptions.FrequencyResolution) => (1, 96),
        nameof(AnalyzerOptions.TimeResolution) => (16, 1024),
        nameof(AnalyzerOptions.SwiftWidthScale) => (.25, 8),
        nameof(AnalyzerOptions.SwiftImbalance) => (0, 1),
        nameof(AnalyzerOptions.HistorySeconds) => (.01, 60),
        nameof(AnalyzerOptions.InputGainDb) => (-60, 60),
        nameof(AnalyzerOptions.TimeWindowSeconds) => (2.0 / Options.TimeResolution, 2),
        nameof(AnalyzerOptions.FrequencyWindowOctaves) => (2.0 / Options.FrequencyResolution, 8),
        _ => throw new InvalidOperationException($"No slider bounds defined for {name}."),
    };

    void Changed() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    sealed class WidthToolTipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            Math.Pow(2, (double)value).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    public void SetScenes(IEnumerable<string> names, string selected)
    {
        refreshingScenes = true;
        try
        {
            scene.Items.Clear();
            foreach (var name in names.Order(StringComparer.Ordinal)) scene.Items.Add(name);
            scene.SelectedItem = selected;
            scene.IsEnabled = scene.Items.Count != 0;
        }
        finally { refreshingScenes = false; }
    }

    public void SetStatus(string text) => status.Text = text;

    public void SetTiming(Session? session)
    {
        if (session == null) { timing.Text = "No active audio session."; return; }
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
        timing.Text = $"Mean / p95, last 256 measurements\n"
            + $"Audio: {format.SampleRate:N0} Hz, {format.ChannelCount} channels\n"
            + $"Analysis: {rate:F1} Hz ({1000 / rate:F2} ms/block)\n"
            + $"Capture to draw: {Stat("render.capture_to_submission_ms")}\n"
            + $"Capture queue: {Stat("capture.queue_ms")}\n"
            + $"Source to draw: {Stat("render.source_to_submission_ms")}\n"
            + $"Audio age at frame start: {Stat("render.presented_audio_age_ms")}\n"
            + $"Timestamp quality: {analyzer.LastTimestampQuality}\n"
            + $"Analysis CPU: {Stat("analysis.total_ms")}\n"
            + $"Draw CPU: {Stat("render.draw_cpu_ms")}\n"
            + $"Audio upload CPU: {Stat("render.audio_upload_ms")}\n"
            + $"GPU frame: {Stat("gpu.frame_ms")}\n"
            + $"GPU fluid: {Stat("gpu.fluid_ms")}\n"
            + $"GPU pressure: {Stat("gpu.fluid_pressure_ms")}\n"
            + $"GPU velocity / material: {Stat("gpu.fluid_velocity_ms")} | {Stat("gpu.fluid_material_ms")}\n"
            + $"GPU scene / output: {Stat("gpu.scene_ms")} | {Stat("gpu.output_ms")}\n"
            + $"Fluid work: {session.Renderer.FluidWork}\n"
            + $"GPU timing: delayed, nonblocking; skipped {session.Renderer.SkippedGpuTimingFrames}, invalid {session.Renderer.InvalidGpuTimingFrames}\n"
            + $"Frame pacing wait: {Stat("render.pacing_wait_ms")}\n"
            + $"Frame clock: {(session.Renderer.LastPresentationTimestamp.HasValue ? "compositor estimate" : "host QPC")}\n"
            + $"Display interval: {Stat("render.display_interval_ms")}\n"
            + $"Present wait: {Stat("render.present_wait_ms")} | {fps}\n"
            + $"Output: {session.Renderer.OutputDescription}\n"
            + $"GPU: {session.Renderer.AdapterDescription}\n"
            + $"History delay: {audio.PresentationDelay * 1000:F0} ms | Drops: {analyzer.DroppedInputCount} input, {audio.DroppedRows} visual\n"
            + $"Lost audio: {analyzer.DroppedAudioMilliseconds:F1} ms | Discontinuities: {analyzer.DiscontinuityCount}\n"
            + "GPU stages are nested; do not sum them. Source ages end at CPU submission; speaker/display delay is not calibrated.";
    }
}

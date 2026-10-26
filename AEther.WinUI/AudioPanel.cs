using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Globalization.NumberFormatting;

namespace AEther.WinUI;

public sealed class AudioPanel : UserControl
{
    public AnalyzerOptions Options { get; } = new();
    internal FrameworkElement GainEditor { get; private set; } = null!;
    public event EventHandler? SettingsChanged;

    public AudioPanel() => Content = BuildOptions();

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
                var displayName = property.Name == nameof(AnalyzerOptions.InputGainDb) ? "Gain" : property.DisplayName;
                FrameworkElement editor;
                if (property.PropertyType == typeof(bool))
                {
                    var toggle = new ToggleSwitch { Header = displayName, IsOn = (bool)property.GetValue(Options)! };
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
                    var combo = new ComboBox { Header = displayName, HorizontalAlignment = HorizontalAlignment.Stretch };
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
                    var noiseFloor = property.Name == nameof(AnalyzerOptions.SmoothingNoiseFloor);
                    var zeroLogarithmic = property.Name is nameof(AnalyzerOptions.LogAccelerationIntensity)
                        or nameof(AnalyzerOptions.ComplexAccelerationIntensity);
                    var logarithmic = property.Name is nameof(AnalyzerOptions.SwiftWidthScale)
                        or nameof(AnalyzerOptions.SmoothingNoiseFloor);
                    var step = integral ? 1 : zeroLogarithmic ? .00001 : noiseFloor ? .0001 : property.Name is nameof(AnalyzerOptions.SwiftWidthScale)
                        or nameof(AnalyzerOptions.SwiftImbalance) ? .01 : .001;
                    double ToSlider(double value) => zeroLogarithmic ? Math.Log2(1 + value / 1e-8) : logarithmic ? Math.Log2(value) : value;
                    double FromSlider(double value) => zeroLogarithmic ? 1e-8 * (Math.Pow(2, value) - 1) : logarithmic ? Math.Pow(2, value) : value;
                    var number = new NumberBox
                    {
                        Header = displayName,
                        Value = Convert.ToDouble(property.GetValue(Options)),
                        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                        SmallChange = step,
                        LargeChange = integral ? 10 : noiseFloor || zeroLogarithmic ? .001 : .1,
                        NumberFormatter = new DecimalFormatter { FractionDigits = integral ? 0 : noiseFloor || zeroLogarithmic ? 12 : 3 },
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Minimum = NumericBounds(property.Name).Minimum,
                        Maximum = NumericBounds(property.Name).Maximum,
                    };
                    var slider = new Slider
                    {
                        Minimum = ToSlider(number.Minimum), Maximum = ToSlider(number.Maximum),
                        StepFrequency = logarithmic || zeroLogarithmic ? .05 : step,
                        SmallChange = logarithmic || zeroLogarithmic ? .05 : number.SmallChange,
                        LargeChange = logarithmic || zeroLogarithmic ? .5 : number.LargeChange,
                        Value = ToSlider(number.Value),
                    };
                    if (logarithmic || zeroLogarithmic) slider.ThumbToolTipValueConverter = new WidthToolTipConverter(zeroLogarithmic ? 1e-8 : 0);
                    AutomationProperties.SetName(number, displayName);
                    AutomationProperties.SetName(slider, displayName);
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
                    void Apply(double value)
                    {
                        if (updating || refreshingNumbers || !double.IsFinite(value)) return;
                        value = integral ? Math.Round(value) : value;
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
                    }
                    SliderCommit.Bind(slider, value =>
                    {
                        if (updating || refreshingNumbers) return;
                        updating = true;
                        try { number.Value = FromSlider(value); }
                        finally { updating = false; }
                    }, value => Apply(FromSlider(value)));
                    number.ValueChanged += (_, e) => Apply(e.NewValue);
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
                AutomationProperties.SetName(editor, displayName);
                if (!string.IsNullOrWhiteSpace(property.Description)) ToolTipService.SetToolTip(editor, property.Description);
                if (property.Name == nameof(AnalyzerOptions.InputGainDb)) GainEditor = editor;
                else rows.Children.Add(editor);
            }
            if (rows.Children.Count == 0) continue;
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
        nameof(AnalyzerOptions.LogAccelerationIntensity) => (0, 1e3),
        nameof(AnalyzerOptions.ComplexAccelerationIntensity) => (0, 1e3),
        nameof(AnalyzerOptions.SmoothingNoiseFloor) => (1e-12, 1),
        nameof(AnalyzerOptions.TimeWindowSeconds) => (2.0 / Options.TimeResolution, 2),
        nameof(AnalyzerOptions.FrequencyWindowOctaves) => (2.0 / Options.FrequencyResolution, 8),
        _ => throw new InvalidOperationException($"No slider bounds defined for {name}."),
    };

    void Changed() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    sealed class WidthToolTipConverter(double zeroScale = 0) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            ((Math.Pow(2, (double)value) - (zeroScale > 0 ? 1 : 0)) * (zeroScale > 0 ? zeroScale : 1))
                .ToString("0.############", System.Globalization.CultureInfo.CurrentCulture);

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

}

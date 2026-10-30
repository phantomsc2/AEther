using AEther.Vortice;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace AEther.WinUI;

/// <summary>Builds ordinary controls from option property types and component metadata.</summary>
internal sealed class OptionsEditor<T> where T : class
{
    readonly Func<T, T> copy;
    readonly Dictionary<PropertyDescriptor, Action> refresh = [];
    bool refreshing;
    public T Options { get; private set; }
    public StackPanel Content { get; } = new() { Spacing = 4 };
    public event EventHandler? Changed;

    public OptionsEditor(T options, Func<T, T> copy, T defaults)
    {
        Options = options;
        this.copy = copy;
        var groups = TypeDescriptor.GetProperties(typeof(T)).Cast<PropertyDescriptor>()
            .Where(p => p.IsBrowsable && !p.IsReadOnly)
            .GroupBy(p => p.Category).ToArray();
        var defaultCategory = CategoryAttribute.Default.Category;
        FrameworkElement Build(PropertyDescriptor property)
        {
            var editor = CreateEditor(property);
            editor.Tag = property.Name;
            AutomationProperties.SetName(editor, property.DisplayName);
            if (!string.IsNullOrWhiteSpace(property.Description)) ToolTipService.SetToolTip(editor, property.Description);
            var defaultValue = property.GetValue(defaults)!;
            ParameterMenu.Attach(editor, () => Set(property, defaultValue));
            return editor;
        }
        foreach (var property in groups.Where(g => g.Key == defaultCategory).SelectMany(g => g))
            Content.Children.Add(Build(property));
        foreach (var category in groups.Where(g => g.Key != defaultCategory).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rows = new StackPanel { Spacing = 8 };
            foreach (var property in category)
                rows.Children.Add(Build(property));
            Content.Children.Add(new Expander
            {
                Header = category.Key, Content = rows, IsExpanded = false,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });
        }
        Refresh();
    }

    public void SetOptions(T options)
    {
        Options = copy(options);
        Refresh();
    }

    void Set(PropertyDescriptor property, object value)
    {
        if (refreshing || Equals(property.GetValue(Options), value)) return;
        var next = copy(Options);
        property.SetValue(next, value);
        // Include aliases changed by a setter, without rewriting unrelated controls.
        var changed = refresh.Where(pair => !Equals(pair.Key.GetValue(Options), pair.Key.GetValue(next)))
            .Select(pair => pair.Value).ToArray();
        Options = next;
        Refresh(changed);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Refresh() => Refresh(refresh.Values);

    void Refresh(IEnumerable<Action> actions)
    {
        refreshing = true;
        try { foreach (var action in actions) action(); }
        finally { refreshing = false; }
    }

    FrameworkElement CreateEditor(PropertyDescriptor property)
    {
        if (property.PropertyType == typeof(bool))
        {
            var toggle = new ToggleSwitch { IsOn = (bool)property.GetValue(Options)! };
            toggle.Toggled += (_, _) => Set(property, toggle.IsOn);
            refresh.Add(property, () => toggle.IsOn = (bool)property.GetValue(Options)!);
            return LabeledControl.Create(property.DisplayName, toggle);
        }
        if (property.PropertyType.IsEnum)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var value in Enum.GetValues(property.PropertyType))
            {
                var label = property.PropertyType.GetField(value.ToString()!)?
                    .GetCustomAttributes(typeof(DescriptionAttribute), false)
                    .OfType<DescriptionAttribute>().FirstOrDefault()?.Description ?? value.ToString();
                combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            }
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem item) Set(property, item.Tag);
            };
            refresh.Add(property, () => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, property.GetValue(Options))));
            return LabeledControl.Create(property.DisplayName, combo);
        }
        if (property.PropertyType != typeof(int) && property.PropertyType != typeof(uint) && property.PropertyType != typeof(double))
            throw new InvalidOperationException($"Unsupported option type: {property.PropertyType}.");
        var range = property.Attributes[typeof(RangeAttribute)] as RangeAttribute
            ?? throw new InvalidOperationException($"Option {property.Name} needs a Range attribute.");
        bool integral = property.PropertyType != typeof(double);
        var min = Convert.ToDouble(range.Minimum);
        var max = Convert.ToDouble(range.Maximum);
        var scale = property.Attributes[typeof(NumericScaleAttribute)] as NumericScaleAttribute;
        bool powers = scale?.PowersOfTwo == true, adaptive = scale?.Adaptive == true;
        double Encode(double n) => powers ? Math.Log2(n) : n;
        double Decode(double n) => powers ? Math.Pow(2, Math.Round(n)) : n;
        double Normalize(double n) => Math.Clamp(powers ? Decode(Encode(Math.Max(min, n))) : integral ? Math.Round(n) : n, min, max);
        var step = integral ? 1 : (max - min) / 1000;
        var initial = Convert.ToDouble(property.GetValue(Options));
        var number = new NumberBox
        {
            Minimum = min, Maximum = max, Value = initial,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = powers ? initial / 2 : step, LargeChange = powers ? initial : step * 10,
            NumberFormatter = new DecimalFormatter { FractionDigits = integral ? 0 : (property.Attributes[typeof(NumericPrecisionAttribute)] as NumericPrecisionAttribute)?.FractionDigits ?? 3 },
        };
        var slider = new Slider
        {
            Minimum = Encode(min), Maximum = Encode(adaptive ? Math.Min(max, Math.Max(min + 16, initial * 4)) : max),
            Value = Encode(initial), StepFrequency = step, SmallChange = step, LargeChange = step * 10,
        };
        bool updating = false;
        void Display(double value)
        {
            updating = true;
            try
            {
                number.Value = value;
                if (powers) { number.SmallChange = value / 2; number.LargeChange = value; }
                slider.Maximum = Math.Max(slider.Maximum, Encode(value));
                slider.Value = Encode(value);
            }
            finally { updating = false; }
        }
        refresh.Add(property, () => Display(Convert.ToDouble(property.GetValue(Options))));
        void Apply(double value)
        {
            if (updating || refreshing || !double.IsFinite(value)) return;
            value = Normalize(value);
            var normalized = Convert.ChangeType(value, property.PropertyType);
            if (Equals(property.GetValue(Options), normalized)) Display(value);
            else Set(property, normalized);
        }
        SliderCommit.Bind(slider, value =>
        {
            if (updating || refreshing) return;
            updating = true;
            try { number.Value = Decode(value); }
            finally { updating = false; }
        }, value => Apply(Decode(value)));
        number.ValueChanged += (_, e) => Apply(e.NewValue);
        return NumericEditor.Create(property.DisplayName, number, slider);
    }
}

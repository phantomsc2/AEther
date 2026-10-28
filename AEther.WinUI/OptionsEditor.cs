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
    readonly List<Action> refresh = [];
    bool refreshing;
    public T Options { get; private set; }
    public StackPanel Content { get; } = new() { Spacing = 4 };
    public event EventHandler? Changed;

    public OptionsEditor(T options, Func<T, T> copy)
    {
        Options = options;
        this.copy = copy;
        var properties = TypeDescriptor.GetProperties(typeof(T)).Cast<PropertyDescriptor>()
            .Where(p => p.IsBrowsable && !p.IsReadOnly);
        foreach (var category in properties.GroupBy(p => p.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var rows = new StackPanel { Spacing = 8 };
            foreach (var property in category)
            {
                var editor = CreateEditor(property);
                AutomationProperties.SetName(editor, property.DisplayName);
                if (!string.IsNullOrWhiteSpace(property.Description)) ToolTipService.SetToolTip(editor, property.Description);
                rows.Children.Add(editor);
            }
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
        Options = next;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void Refresh()
    {
        refreshing = true;
        try { foreach (var action in refresh) action(); }
        finally { refreshing = false; }
    }

    FrameworkElement CreateEditor(PropertyDescriptor property)
    {
        if (property.PropertyType == typeof(bool))
        {
            var toggle = new ToggleSwitch { IsOn = (bool)property.GetValue(Options)! };
            toggle.Toggled += (_, _) => Set(property, toggle.IsOn);
            refresh.Add(() => toggle.IsOn = (bool)property.GetValue(Options)!);
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
            refresh.Add(() => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, property.GetValue(Options))));
            return LabeledControl.Create(property.DisplayName, combo);
        }
        if (property.PropertyType != typeof(int) && property.PropertyType != typeof(double))
            throw new InvalidOperationException($"Unsupported option type: {property.PropertyType}.");
        var range = property.Attributes[typeof(RangeAttribute)] as RangeAttribute
            ?? throw new InvalidOperationException($"Option {property.Name} needs a Range attribute.");
        bool integral = property.PropertyType == typeof(int);
        var min = Convert.ToDouble(range.Minimum);
        var max = Convert.ToDouble(range.Maximum);
        var step = integral ? 1 : (max - min) / 1000;
        var number = new NumberBox
        {
            Minimum = min, Maximum = max, Value = Convert.ToDouble(property.GetValue(Options)),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = step, LargeChange = step * 10,
            NumberFormatter = new DecimalFormatter { FractionDigits = integral ? 0 : 12 },
        };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, Value = number.Value,
            StepFrequency = step, SmallChange = step, LargeChange = step * 10,
        };
        bool updating = false;
        refresh.Add(() =>
        {
            var value = Convert.ToDouble(property.GetValue(Options));
            number.Value = value;
            slider.Value = value;
        });
        void Apply(double value)
        {
            if (updating || refreshing || !double.IsFinite(value)) return;
            value = Math.Clamp(integral ? Math.Round(value) : value, min, max);
            updating = true;
            try { number.Value = value; slider.Value = value; }
            finally { updating = false; }
            Set(property, Convert.ChangeType(value, property.PropertyType));
        }
        SliderCommit.Bind(slider, value =>
        {
            if (updating || refreshing) return;
            updating = true;
            try { number.Value = value; }
            finally { updating = false; }
        }, Apply);
        number.ValueChanged += (_, e) => Apply(e.NewValue);
        return NumericEditor.Create(property.DisplayName, number, slider);
    }
}

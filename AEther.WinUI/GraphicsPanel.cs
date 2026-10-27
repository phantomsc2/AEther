using AEther.Vortice;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace AEther.WinUI;

public sealed class GraphicsPanel : UserControl
{
    readonly NumberBox number = new()
    {
        Minimum = .01, Maximum = 60, Value = 2, SmallChange = .001, LargeChange = .1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        NumberFormatter = new DecimalFormatter { FractionDigits = 3 },
    };
    readonly Slider slider = new() { Minimum = .01, Maximum = 60, Value = 2, StepFrequency = .001,
        SmallChange = .001, LargeChange = .1 };
    bool updating;
    public GraphicsOptions Options { get; private set; } = new();
    public event EventHandler? SettingsChanged;

    public GraphicsPanel()
    {
        Content = NumericEditor.Create("HistorySeconds", number, slider);
        ToolTipService.SetToolTip(this, "Seconds of graphics history and material decay. Changing this clears the histogram without restarting analysis.");
        SliderCommit.Bind(slider, value =>
        {
            if (updating) return;
            updating = true;
            try { number.Value = value; }
            finally { updating = false; }
        }, Apply);
        number.ValueChanged += (_, e) => Apply(e.NewValue);
    }

    public void SetOptions(GraphicsOptions options)
    {
        updating = true;
        try { Options = options; number.Value = options.HistorySeconds; slider.Value = options.HistorySeconds; }
        finally { updating = false; }
    }

    void Apply(double value)
    {
        if (updating || !double.IsFinite(value)) return;
        value = Math.Clamp(value, .01, 60);
        var next = new GraphicsOptions(value);
        if (next == Options) return;
        SetOptions(next);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}

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
    readonly NumberBox aggregationNumber = new()
    {
        Minimum = 0, Maximum = 1, Value = .5, SmallChange = .01, LargeChange = .1,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        NumberFormatter = new DecimalFormatter { FractionDigits = 3 },
    };
    readonly Slider aggregationSlider = new() { Minimum = 0, Maximum = 1, Value = .5,
        StepFrequency = .01, SmallChange = .01, LargeChange = .1 };
    internal FrameworkElement SpectrumAggregationEditor { get; }
    bool updating;
    public GraphicsOptions Options { get; private set; } = new();
    public event EventHandler? SettingsChanged;

    public GraphicsPanel()
    {
        var history = NumericEditor.Create("HistorySeconds", number, slider);
        Content = history;
        SpectrumAggregationEditor = NumericEditor.Create("Peak emphasis", aggregationNumber, aggregationSlider);
        ToolTipService.SetToolTip(history, "Seconds of graphics history and material decay. Changing this clears the histogram without restarting analysis.");
        ToolTipService.SetToolTip(SpectrumAggregationEditor, "Spectrum aggregation: 0 = Mean, 0.5 = RMS, 1 = Max. Uses each analysis row's overlap with the display interval. Spectrum and Debug diagnostics always show newly delivered blocks.");
        SliderCommit.Bind(aggregationSlider, value =>
        {
            if (updating) return;
            updating = true;
            try { aggregationNumber.Value = value; }
            finally { updating = false; }
        }, ApplyAggregation);
        aggregationNumber.ValueChanged += (_, e) => ApplyAggregation(e.NewValue);
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
        try
        {
            Options = options; number.Value = options.HistorySeconds; slider.Value = options.HistorySeconds;
            aggregationNumber.Value = options.SpectrumAggregation; aggregationSlider.Value = options.SpectrumAggregation;
        }
        finally { updating = false; }
    }

    void Apply(double value)
    {
        if (updating || !double.IsFinite(value)) return;
        value = Math.Clamp(value, .01, 60);
        var next = Options with { HistorySeconds = value };
        if (next == Options) return;
        SetOptions(next);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    void ApplyAggregation(double value)
    {
        if (updating || !double.IsFinite(value)) return;
        var next = Options with { SpectrumAggregation = Math.Clamp(value, 0, 1) };
        if (next == Options) return;
        SetOptions(next);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}

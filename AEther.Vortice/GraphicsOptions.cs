using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AEther.Vortice;

/// <summary>Renderer-owned retention, visual time scale and spectrum presentation, independent of analysis.</summary>
public sealed record GraphicsOptions(
    [property: Category("Graphics"), Range(.01, 60.0)]
    [property: Description("Seconds of graphics history and material decay. Changing this clears the histogram without restarting analysis.")]
    double HistorySeconds = 2,
    [property: Category("Graphics"), DisplayName("Peak emphasis"), Range(0.0, 1.0)]
    [property: Description("Spectrum aggregation: 0 = Mean, 0.5 = RMS, 1 = Max. Uses each analysis row's overlap with the display interval. Spectrum and Debug diagnostics always show newly delivered blocks.")]
    double SpectrumAggregation = .5)
{
    public void Validate()
    {
        if (!double.IsFinite(HistorySeconds) || HistorySeconds < .01 || HistorySeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(HistorySeconds));
        if (!double.IsFinite(SpectrumAggregation) || SpectrumAggregation < 0 || SpectrumAggregation > 1)
            throw new ArgumentOutOfRangeException(nameof(SpectrumAggregation));
    }
}

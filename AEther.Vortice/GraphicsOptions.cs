using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace AEther.Vortice;

/// <summary>Renderer-owned retention, spectrum presentation and exposure, independent of analysis.</summary>
public sealed record GraphicsOptions(
    [property: Range(.01, 60.0)]
    [property: Description("Seconds of graphics history and material decay. Changing this clears the histogram without restarting analysis.")]
    double HistorySeconds = 2,
    [property: DisplayName("Peak emphasis"), Range(0.0, 1.0)]
    [property: Description("Spectrum aggregation: 0 = Mean, 0.5 = RMS, 1 = Max. Uses each analysis row's overlap with the display interval. Spectrum and Debug diagnostics always show newly delivered blocks.")]
    double SpectrumAggregation = .5,
    [property: Range(-12.0, 12.0)]
    [property: Description("Exposure in stops: +1 doubles illumination, -1 halves it. Applies to linear scene output; diagnostics keep their original colors.")]
    double Exposure = 0)
{
    public void Validate()
    {
        if (!double.IsFinite(HistorySeconds) || HistorySeconds < .01 || HistorySeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(HistorySeconds));
        if (!double.IsFinite(SpectrumAggregation) || SpectrumAggregation < 0 || SpectrumAggregation > 1)
            throw new ArgumentOutOfRangeException(nameof(SpectrumAggregation));
        if (!double.IsFinite(Exposure) || Exposure < -12 || Exposure > 12)
            throw new ArgumentOutOfRangeException(nameof(Exposure));
    }
}

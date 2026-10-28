namespace AEther.Vortice;

/// <summary>Renderer-owned retention, visual time scale and spectrum presentation, independent of analysis.</summary>
public sealed record GraphicsOptions(double HistorySeconds = 2, double SpectrumAggregation = .5)
{
    internal void Validate()
    {
        if (!double.IsFinite(HistorySeconds) || HistorySeconds < .01 || HistorySeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(HistorySeconds));
        if (!double.IsFinite(SpectrumAggregation) || SpectrumAggregation < 0 || SpectrumAggregation > 1)
            throw new ArgumentOutOfRangeException(nameof(SpectrumAggregation));
    }
}

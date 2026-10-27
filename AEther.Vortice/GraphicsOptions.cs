namespace AEther.Vortice;

/// <summary>Renderer-owned retention and visual time scale, independent of analysis.</summary>
public sealed record GraphicsOptions(double HistorySeconds = 2)
{
    internal void Validate()
    {
        if (!double.IsFinite(HistorySeconds) || HistorySeconds < .01 || HistorySeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(HistorySeconds));
    }
}

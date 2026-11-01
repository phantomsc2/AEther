using System;

namespace AEther.WinUI;

/// <summary>Streaming statistics for one display window, without retaining samples.</summary>
internal struct TimingSummary
{
    internal long Count { get; private set; }
    internal double Mean { get; private set; }
    internal double Max { get; private set; }
    double m2;

    internal double StandardDeviation => Count == 0 ? 0 : Math.Sqrt(Math.Max(0, m2 / Count));

    internal void Add(double value)
    {
        if (!double.IsFinite(value) || value < 0) return;
        Count++;
        double delta = value - Mean;
        Mean += delta / Count;
        m2 += delta * (value - Mean);
        Max = Math.Max(Max, value);
    }

    internal void Reset() => this = default;
}

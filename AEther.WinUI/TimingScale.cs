using System;

namespace AEther.WinUI;

/// <summary>Outward 1-2-5 logarithmic bounds with immediate expansion and delayed contraction.</summary>
internal sealed class TimingScale
{
    internal static readonly double[] Steps = [0, Math.Log10(2), Math.Log10(5)];
    internal double? Lower { get; private set; }
    internal double? Upper { get; private set; }
    double? lowerCandidate, upperCandidate, lowerSince, upperSince;

    internal void Reset()
    {
        Lower = Upper = lowerCandidate = upperCandidate = lowerSince = upperSince = null;
    }

    internal static double Floor(double value)
    {
        double decade = Math.Floor(value), fraction = value - decade;
        return decade + (fraction >= Steps[2] ? Steps[2] : fraction >= Steps[1] ? Steps[1] : 0);
    }

    internal static double Ceiling(double value)
    {
        double floor = Floor(value);
        return floor >= value ? floor : Next(floor);
    }

    internal static double Next(double value)
    {
        double decade = Math.Floor(value), fraction = value - decade;
        if (fraction < Steps[1] - 1e-12) return decade + Steps[1];
        if (fraction < Steps[2] - 1e-12) return decade + Steps[2];
        return decade + 1;
    }

    internal bool Update(double min, double max, double now)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || min > max)
        {
            lowerCandidate = upperCandidate = lowerSince = upperSince = null;
            return false;
        }
        double targetLower = Floor(min), targetUpper = Ceiling(max);
        if (targetUpper <= targetLower) targetUpper = Next(targetLower);
        if (Lower == null || Upper == null)
        {
            Lower = targetLower; Upper = targetUpper;
            return true;
        }
        double oldLower = Lower.Value, oldUpper = Upper.Value;
        if (targetLower < oldLower) { Lower = targetLower; lowerCandidate = lowerSince = null; }
        else if (targetLower > oldLower)
        {
            if (lowerCandidate == null || min < lowerCandidate)
            { lowerCandidate = targetLower; lowerSince = now; }
            if (now - lowerSince >= 3) { Lower = lowerCandidate; lowerCandidate = lowerSince = null; }
        }
        else lowerCandidate = lowerSince = null;

        if (targetUpper > oldUpper) { Upper = targetUpper; upperCandidate = upperSince = null; }
        else if (targetUpper < oldUpper)
        {
            if (upperCandidate == null || max > upperCandidate)
            { upperCandidate = targetUpper; upperSince = now; }
            if (now - upperSince >= 3) { Upper = upperCandidate; upperCandidate = upperSince = null; }
        }
        else upperCandidate = upperSince = null;
        if (Lower >= Upper) { Upper = Next(Lower!.Value); upperCandidate = upperSince = null; }
        // Both proposed bounds were derived from all retained samples.
        return Lower != oldLower || Upper != oldUpper;
    }

    internal static double[] Ticks(double lower, double upper)
    {
        var values = new System.Collections.Generic.List<double>();
        for (double tick = lower; tick <= upper + 1e-12; tick = Next(tick))
            values.Add(tick);
        return values.ToArray();
    }
}

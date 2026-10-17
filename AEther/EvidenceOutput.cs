using System;

namespace AEther;

/// <summary>Positive S/T projection in input-gained amplitude units. Signed deviations remain
/// intact inside the estimators; background B and raw amplitude are emitted directly.</summary>
internal readonly struct EvidenceOutput
{
    public static void Write(double sine, double transient, double background, double amplitude, Span<double> output)
    {
        output[0] = Math.Max(0, sine);
        output[1] = Math.Max(0, transient);
        output[2] = background;
        output[3] = amplitude;
    }
}

using System;

namespace AEther;

/// <summary>Display gains and positive S/T projection. Signed deviations remain
/// intact inside the estimators; background B and raw amplitude are emitted directly.</summary>
internal readonly struct EvidenceOutput
{
    readonly double SineGain, TransientGain, BackgroundGain;

    public EvidenceOutput(AnalyzerOptions options)
    {
        SineGain = options.SineGain;
        TransientGain = options.TransientGain;
        BackgroundGain = options.BackgroundGain;
    }

    public void Write(double sine, double transient, double background, double amplitude, Span<double> output)
    {
        output[0] = SineGain * Math.Max(0, sine);
        output[1] = TransientGain * Math.Max(0, transient);
        output[2] = BackgroundGain * background;
        output[3] = amplitude;
    }
}

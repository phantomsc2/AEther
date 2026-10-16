using System;

namespace AEther;

/// <summary>Display gains and positive S/T projection. Signed deviations remain
/// intact inside the estimators; background N and raw amplitude are emitted directly.</summary>
internal readonly struct EvidenceOutput
{
    readonly double SineGain, TransientGain, NoiseGain;

    public EvidenceOutput(AnalyzerOptions options)
    {
        SineGain = options.SineGain;
        TransientGain = options.TransientGain;
        NoiseGain = options.NoiseGain;
    }

    public void Write(double sine, double transient, double noise, double amplitude, Span<double> output)
    {
        output[0] = SineGain * Math.Max(0, sine);
        output[1] = TransientGain * Math.Max(0, transient);
        output[2] = NoiseGain * noise;
        output[3] = amplitude;
    }
}

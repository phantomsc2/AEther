using System;

namespace AEther;

/// <summary>Signed Gaussian effects evaluated at the current bin/frame and residual RMS.
/// Standardized scores are effect sizes, not probabilities or estimator standard errors.</summary>
public readonly record struct JointEvidence(double Sine, double Transient, double Background,
    double ResidualRms, double Amplitude, int FrequencySamples, int TimeSamples)
{
    public double Scale => ResidualRms;
    public int ResidualDegreesOfFreedom => Math.Max(0, FrequencySamples - 1) * Math.Max(0, TimeSamples - 1);
    public bool HasResidualScale => Scale > 0;
    public double NormalizedSine => Standardize(Sine, Scale);
    public double NormalizedTransient => Standardize(Transient, Scale);

    static double Standardize(double effect, double scale) => scale > 0 ? effect / scale
        : effect == 0 ? 0 : Math.CopySign(double.PositiveInfinity, effect);
}

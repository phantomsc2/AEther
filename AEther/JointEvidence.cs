using System;

namespace AEther;

/// <summary>Signed Gaussian effects evaluated at the current bin/frame and residual RMS.
/// Standardized scores are effect sizes, not probabilities or estimator standard errors.</summary>
public readonly record struct JointEvidence(double Sine, double Transient, double Background,
    double ResidualRms, double Amplitude, int FrequencySamples, int TimeSamples)
{
    /// <summary>Directional slope coefficients in amplitude per second and per octave.</summary>
    public double SineTimeSlope { get; init; }
    public double TransientFrequencySlope { get; init; }
    /// <summary>Dimensions occupied on each axis by the shared polynomial basis.</summary>
    public int TimeBasisRank { get; init; } = 1;
    public int FrequencyBasisRank { get; init; } = 1;
    public double Scale => ResidualRms;
    public int ResidualDegreesOfFreedom => (FrequencySamples - FrequencyBasisRank) * (TimeSamples - TimeBasisRank);
    public bool HasResidualScale => Scale > 0;
    public double NormalizedSine => Standardize(Sine, Scale);
    public double NormalizedTransient => Standardize(Transient, Scale);

    static double Standardize(double effect, double scale) => scale > 0 ? effect / scale
        : effect == 0 ? 0 : Math.CopySign(double.PositiveInfinity, effect);
}

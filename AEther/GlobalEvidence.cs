namespace AEther;

/// <summary>Full-domain directional fit of channel-averaged linear magnitudes.
/// Background is the rectangle mean, Transient is the signed current mean minus
/// Background, Structure is RMS of the current signed S field, and InteractionRms
/// is residual RMS over the complete rectangle (not a noise classification).
/// Structure + Transient + Background is not an additive level decomposition.</summary>
public readonly record struct GlobalEvidence(double Background, double Transient, double Structure,
    double InteractionRms, int FrequencySamples, int TimeSamples)
{
    public double CurrentMean => Background + Transient;
    public int TimeBasisRank { get; init; } = 1;
    public int FrequencyBasisRank { get; init; } = 1;
    public int ResidualDegreesOfFreedom => (FrequencySamples - FrequencyBasisRank) * (TimeSamples - TimeBasisRank);
}

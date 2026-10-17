using System;

namespace AEther;

/// <summary>One shared fit, with the same causal support and slopes as the local fits.
/// Reuses their numerical kernel with explicit full-domain neighborhoods.</summary>
public sealed class GlobalSplitter
{
    readonly Splitter fit;
    readonly double[] mean, output;
    public GlobalEvidence Evidence { get; private set; }
    /// <summary>Current signed global fields, valid until the next Process/Reset.</summary>
    public ReadOnlyMemory<JointEvidence> Estimates => fit.Estimates;

    public GlobalSplitter(Domain domain, AnalyzerOptions options, double? analysisRate = null)
    {
        fit = new(domain, options, analysisRate, fullDomain: true);
        mean = new double[domain.Length];
        output = new double[domain.Length * Splitter.OutputStride];
    }

    /// <summary>Channel-major gained linear magnitudes, averaged before fitting.
    /// Magnitude averaging preserves opposite-phase stereo and mono duplication.</summary>
    public GlobalEvidence Process(ReadOnlyMemory<double> channels, int channelCount)
    {
        if (channelCount < 1 || channels.Length != checked(channelCount * mean.Length))
            throw new ArgumentException("Global spectrum dimensions do not match.");
        for (var f = 0; f < mean.Length; ++f)
        {
            double maximum = 0;
            for (var c = 0; c < channelCount; ++c)
                maximum = Math.Max(maximum, SpectralMath.Sanitize(channels.Span[c * mean.Length + f]));
            double sum = 0, correction = 0;
            if (maximum > 0)
                for (var c = 0; c < channelCount; ++c)
                {
                    var value = SpectralMath.Sanitize(channels.Span[c * mean.Length + f]) / maximum;
                    var next = sum + value;
                    correction += Math.Abs(sum) >= value ? (sum - next) + value : (value - next) + sum;
                    sum = next;
                }
            mean[f] = Math.Clamp((sum + correction) / channelCount, 0, 1) * maximum;
        }
        fit.ProcessLinear(mean, output);
        return Evidence = fit.GlobalSummary();
    }

    public void Reset() { fit.Reset(); Evidence = default; }
}

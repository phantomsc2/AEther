using System;

namespace AEther
{
    public readonly record struct SpectralControls(double LowSustain, double HighTransient);

    /// <summary>Reduce the time/frequency separation into low sustained and high transient controls.</summary>
    public sealed class SpectralControlExtractor
    {
        readonly double[] LowWeights;
        readonly double[] HighWeights;
        readonly int Channels;

        public SpectralControlExtractor(Domain domain, int channels)
        {
            Channels = channels;
            LowWeights = new double[domain.Length];
            HighWeights = new double[domain.Length];
            var k = 0;
            foreach (var frequency in domain)
            {
                LowWeights[k] = 1 / (1 + Math.Pow(frequency / 250, 4));
                HighWeights[k] = 1 - 1 / (1 + Math.Pow(frequency / 1600, 4));
                k++;
            }
        }

        public SpectralControls Process(SampleEvent<double> input)
        {
            double low = 0, weight = 0, high = 0;
            for (var c = 0; c < Channels; ++c)
            {
                var channel = input.GetChannel(c).Span;
                var stride = input.SampleCount / LowWeights.Length;
                if (stride != 4 && stride != Splitter.OutputStride) throw new ArgumentException("Invalid spectral layout.");
                for (var k = 0; k < LowWeights.Length; ++k)
                {
                    var sustained = Math.Max(0, channel[stride * k]);
                    low += sustained * sustained * LowWeights[k];
                    weight += LowWeights[k];
                    high = Math.Max(high, Math.Max(0, channel[stride * k + 1]) * HighWeights[k]);
                }
            }
            return new(Math.Sqrt(low / Math.Max(1e-12, weight)), high);
        }
    }
}

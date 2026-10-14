using System;
using System.Linq;

namespace AEther
{
    /// <summary>Visual energy proxy from retained spectral levels; no raw residual bypass.</summary>
    public sealed class RetainedVisualFeatures
    {
        readonly double[] Frequencies;
        readonly int Channels;
        readonly double SecondsPerRow;
        double MusicalTime;
        public double Transient { get; private set; }

        public RetainedVisualFeatures(Domain domain, int channels, double analysisRate)
        {
            Frequencies = domain.ToArray(); Channels = channels; SecondsPerRow = 1 / analysisRate;
        }

        public AudioFeatures Process(SampleEvent<double> input)
        {
            double power = 0, low = 0, body = 0, air = 0, left = 0, right = 0;
            Transient = 0;
            for (var c = 0; c < Channels; ++c)
            {
                var channel = input.GetChannel(c).Span;
                double channelPower = 0;
                for (var k = 0; k < Frequencies.Length; ++k)
                {
                    var level = Math.Clamp(channel[4 * k + 3], 0, 1);
                    var squared = level > 0 ? Math.Pow(10, 10 * (level - 1)) : 0;
                    channelPower += squared;
                    if (Frequencies[k] < 160) low += squared;
                    else if (Frequencies[k] < 1600) body += squared;
                    else air += squared;
                    Transient = Math.Max(Transient, channel[4 * k + 1]);
                }
                power += channelPower;
                if (c == 0) left = channelPower;
                if (c == 1) right = channelPower;
            }
            // Positive-frequency amplitude to a power proxy. Overlapping bins
            // make this an artistic control, not a calibrated waveform RMS.
            var divisor = 2.0 * Channels;
            MusicalTime += power / divisor * SecondsPerRow;
            return new(Math.Sqrt(power / divisor), Math.Sqrt(low / divisor), Math.Sqrt(body / divisor),
                Math.Sqrt(air / divisor), Channels > 1 ? (right - left) / Math.Max(1e-12, right + left) : 0)
                { MusicalTime = MusicalTime };
        }
    }
}

using System;

namespace AEther
{
    /// <summary>Measured level and broad frequency-band levels. These are not instrument labels.</summary>
    public readonly record struct AudioFeatures(double Level, double Bass, double Body, double Air, double Stereo)
    {
        /// <summary>Integral of channel-mean sample power in audio seconds; stationary in silence.</summary>
        public double MusicalTime { get; init; }
    }

    public sealed class AudioFeatureExtractor
    {
        readonly double LowCoefficient;
        readonly double BodyCoefficient;
        readonly double[] Low;
        readonly double[] Upper;
        readonly double SampleRate;
        double MusicalTime;

        // MusicalTime is an integral of observed energy and survives missing input.
        public void Reset() { Array.Clear(Low); Array.Clear(Upper); }

        public AudioFeatureExtractor(SampleFormat format)
        {
            if (format.SampleRate <= 0 || format.ChannelCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(format));
            LowCoefficient = 1 - Math.Exp(-2 * Math.PI * 160 / format.SampleRate);
            BodyCoefficient = 1 - Math.Exp(-2 * Math.PI * 1600 / format.SampleRate);
            Low = new double[format.ChannelCount];
            Upper = new double[format.ChannelCount];
            SampleRate = format.SampleRate;
        }

        public AudioFeatures Process(SampleEvent<double> input)
        {
            if (input.SampleCount <= 0)
                return new AudioFeatures { MusicalTime = MusicalTime };
            double level = 0, bass = 0, body = 0, air = 0, left = 0, right = 0;
            for (var c = 0; c < Low.Length; ++c)
            {
                var channel = input.GetChannel(c).Span;
                var channelPower = 0.0;
                foreach (var value in channel)
                {
                    var sample = double.IsFinite(value) ? value : 0;
                    Low[c] += LowCoefficient * (sample - Low[c]);
                    Upper[c] += BodyCoefficient * (sample - Upper[c]);
                    var middle = Upper[c] - Low[c];
                    var high = sample - Upper[c];
                    channelPower += sample * sample;
                    bass += Low[c] * Low[c];
                    body += middle * middle;
                    air += high * high;
                }
                level += channelPower;
                if (c == 0) left = channelPower;
                if (c == 1) right = channelPower;
            }
            var count = input.SampleCount * (double)Low.Length;
            var stereo = Low.Length > 1 ? (right - left) / Math.Max(1e-12, right + left) : 0;
            MusicalTime += level / (Low.Length * SampleRate);
            return new(Math.Sqrt(level / count), Math.Sqrt(bass / count), Math.Sqrt(body / count),
                Math.Sqrt(air / count), stereo) { MusicalTime = MusicalTime };
        }
    }
}

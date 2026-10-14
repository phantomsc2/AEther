using System;

namespace AEther
{
    /// <summary>Immediate amplitude attacks, independent of the long windows used for tonal analysis.</summary>
    public sealed class TransientDetector
    {
        readonly double Release;
        double Envelope;

        public TransientDetector(int sampleRate)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            Release = Math.Exp(-1.0 / (.04 * sampleRate));
        }

        public double Process(ReadOnlySpan<double> samples)
        {
            var attack = 0.0;
            foreach (var sample in samples)
            {
                var amplitude = double.IsFinite(sample) ? Math.Abs(sample) : 0;
                attack = Math.Max(attack, amplitude - Envelope);
                Envelope = Math.Max(amplitude, Envelope * Release);
            }
            return Math.Clamp(attack, 0, 1);
        }
    }
}

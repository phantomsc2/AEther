using System;
using System.Numerics;

namespace AEther
{
    public class DFTFilter : IDFTFilter
    {

        public int Length => Buffer.Length;

        Complex State;
        int nonzero;

        readonly Complex Coefficient;
        readonly double Frequency;
        readonly double DFTFrequency;
        readonly double Q;
        readonly int M;
        readonly RingBuffer<double> Buffer;

        public DFTFilter(double frequency, double frequencyResolution, double sampleRate)
        {

            Frequency = frequency;
            DFTFrequency = Frequency / sampleRate;
            Q = 1.0 / (Math.Pow(2, 1.0 / frequencyResolution) - 1);
            M = (int)Math.Round(Q);
            State = Complex.Zero;
            var length = (int)Math.Round(M / DFTFrequency);
            Buffer = new(length);
            Coefficient = Complex.Exp(2 * Math.PI * Complex.ImaginaryOne * M / Length);

        }

        public Complex GetOutput()
        {
            return State / Buffer.Length;
        }

        public void Reset() { Buffer.Reset(); State = Complex.Zero; nonzero = 0; }

        public void Process(double newSample)
        {
            var oldSample = Buffer.Add(newSample);
            var input = newSample - oldSample;
            State = Coefficient * State + input;
            if (oldSample != 0) nonzero--;
            if (newSample != 0) nonzero++;
            if (nonzero == 0) State = Complex.Zero;
        }

        public void Process(ReadOnlyMemory<double> samples)
        {
            while (0 < samples.Length)
            {

                var dst = Buffer.GetMemory(samples.Length);

                for (var i = 0; i < dst.Length; ++i)
                {
                    var input = samples.Span[i] - dst.Span[i];
                    State = Coefficient * State + input;
                    if (dst.Span[i] != 0) nonzero--;
                    if (samples.Span[i] != 0) nonzero++;
                    if (nonzero == 0) State = Complex.Zero;
                }

                samples[0..dst.Length].CopyTo(dst);
                samples = samples[dst.Length..];

            }
        }

    }
}

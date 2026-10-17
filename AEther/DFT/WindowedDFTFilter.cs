using System;
using System.Numerics;

namespace AEther
{
    public class WindowedDFTFilter : IDFTFilter
    {

        public int Length => Buffer.Length;

        readonly Complex[] States;
        readonly Complex[] Coefficients;
        readonly double Frequency;
        readonly double DFTFrequency;
        readonly double Q;
        readonly int M;
        readonly double[] Window;
        readonly RingBuffer<double> Buffer;
        int nonzero;

        public WindowedDFTFilter(double frequency, double frequencyResolution, double sampleRate, double[] window)
        {

            Window = window;
            Frequency = frequency;
            DFTFrequency = Frequency / sampleRate;
            Q = 1.0 / (Math.Pow(2, 1.0 / frequencyResolution) - 1);
            M = (int)Math.Round(Q);
            States = new Complex[Window.Length];
            var length = (int)Math.Round(M / DFTFrequency);
            Buffer = new(length);
            Coefficients = new Complex[Window.Length];
            for (int j = 0; j < Window.Length; ++j)
            {
                var bin = M - (Window.Length / 2) + j;
                Coefficients[j] = Complex.Exp(2 * Complex.ImaginaryOne * Math.PI * bin / Length);
            }

        }

        public Complex GetOutput()
        {
            Complex result = Complex.Zero;
            for (int j = 0; j < Window.Length; ++j)
            {
                result += Window[j] * States[j];
            }
            return result / (Buffer.Length * Window[Window.Length / 2]);
        }

        public void Process(double newSample)
        {
            var oldSample = Buffer.Add(newSample);
            var input = newSample - oldSample;
            for (int j = 0; j < Window.Length; ++j)
            {
                States[j] = Coefficients[j] * States[j] + input;
            }
            if (oldSample != 0) nonzero--;
            if (newSample != 0) nonzero++;
            if (nonzero == 0) Array.Clear(States);
        }

        public void Process(ReadOnlyMemory<double> samples)
        {
            while (0 < samples.Length)
            {
                var dst = Buffer.GetMemory(samples.Length);
                for (var i = 0; i < dst.Length; ++i)
                {
                    var input = samples.Span[i] - dst.Span[i];
                    for (int j = 0; j < Window.Length; ++j)
                    {
                        States[j] = Coefficients[j] * States[j] + input;
                    }
                    if (dst.Span[i] != 0) nonzero--;
                    if (samples.Span[i] != 0) nonzero++;
                    if (nonzero == 0) Array.Clear(States);
                }
                samples[0..dst.Length].CopyTo(dst);
                samples = samples[dst.Length..];
            }
        }
    }
}

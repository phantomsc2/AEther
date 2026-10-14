using System;
using System.Linq;
using System.Numerics;

namespace AEther
{
    public class WindowedDFTFilterSIMD : IDFTFilter
    {

        public int Length => Buffer.Length;

        ComplexVector<double> States;
        readonly ComplexVector<double> Coefficients;
        readonly double Frequency;
        readonly double DFTFrequency;
        readonly double Q;
        readonly int M;
        readonly Vector<double> Window;
        readonly int WindowSize;
        readonly RingBuffer<double> Buffer;

        public WindowedDFTFilterSIMD(double frequency, double frequencyResolution, double sampleRate, double[] window)
        {

            WindowSize = window.Length;
            var windowPadded = window.Concat(Enumerable.Repeat(0.0, Vector<double>.Count - window.Length)).ToArray();
            Window = new Vector<double>(windowPadded);
            Frequency = frequency;
            DFTFrequency = Frequency / sampleRate;
            Q = 1.0 / (Math.Pow(2, 1.0 / frequencyResolution) - 1);
            M = (int)Math.Round(Q);
            States = ComplexVector<double>.Zero;
            var length = (int)Math.Round(M / DFTFrequency);
            Buffer = new(length);
            Coefficients = ComplexVector<double>.Zero;
            var coeff = new Complex[ComplexVector<double>.Count];
            for (int j = 0; j < window.Length; ++j)
            {
                var bin = M - (window.Length / 2) + j;
                coeff[j] = Complex.Exp(2 * Math.PI * Complex.ImaginaryOne * bin / Length);
            }
            var coeffRe = new Vector<double>(coeff.Select(c => c.Real).ToArray());
            var coeffIm = new Vector<double>(coeff.Select(c => c.Imaginary).ToArray());
            Coefficients = new ComplexVector<double>(coeffRe, coeffIm);

        }

        public Complex GetOutput()
        {
            var windowed = Window * States;
            Complex result = Complex.Zero;
            for (int j = 0; j < WindowSize; ++j)
            {
                var (re, im) = windowed[j];
                result += new Complex(re, im);
            }
            return result / (Buffer.Length * Window[WindowSize / 2]);
        }

        public void Process(double newSample)
        {
            var oldSample = Buffer.Add(newSample);
            var input = new Vector<double>(newSample - oldSample);
            States = Coefficients * States + input;
        }

        public void Process(ReadOnlyMemory<double> samples)
        {
            while (0 < samples.Length)
            {
                var dst = Buffer.GetMemory(samples.Length);
                for (var i = 0; i < dst.Length; ++i)
                {
                    var input = new Vector<double>(samples.Span[i] - dst.Span[i]);
                    States = Coefficients * States + input;
                }
                samples[0..dst.Length].CopyTo(dst);
                samples = samples[dst.Length..];
            }
        }
    }
}

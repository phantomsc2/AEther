using System;
using System.Numerics;

namespace AEther
{
    /// <summary>Exact sliding DFT with SIMD across window components.</summary>
    public class WindowedDFTFilterSIMD : IDFTFilter
    {
        public int Length => Buffer.Length;

        readonly Vector<double>[] Real, Imaginary, CoefficientReal, CoefficientImaginary, Window;
        readonly int WindowSize;
        readonly double Normalization;
        readonly RingBuffer<double> Buffer;
        int nonzero;

        bool IsEmpty(double incoming, double outgoing)
        {
            if (outgoing != 0) nonzero--;
            if (incoming != 0) nonzero++;
            return nonzero == 0;
        }

        public WindowedDFTFilterSIMD(double frequency, double frequencyResolution, double sampleRate, double[] window)
        {
            ArgumentNullException.ThrowIfNull(window);
            if (window.Length == 0) throw new ArgumentException("Window must not be empty.", nameof(window));
            WindowSize = window.Length;
            var groups = (window.Length + Vector<double>.Count - 1) / Vector<double>.Count;
            Real = new Vector<double>[groups];
            Imaginary = new Vector<double>[groups];
            CoefficientReal = new Vector<double>[groups];
            CoefficientImaginary = new Vector<double>[groups];
            Window = new Vector<double>[groups];
            var m = (int)Math.Round(1.0 / (Math.Pow(2, 1.0 / frequencyResolution) - 1));
            Buffer = new((int)Math.Round(m / (frequency / sampleRate)));
            Normalization = Length * window[window.Length / 2];
            var re = new double[Vector<double>.Count];
            var im = new double[Vector<double>.Count];
            var weights = new double[Vector<double>.Count];
            for (var g = 0; g < groups; ++g)
            {
                Array.Clear(re);
                Array.Clear(im);
                Array.Clear(weights);
                for (var lane = 0; lane < Vector<double>.Count; ++lane)
                {
                    var j = g * Vector<double>.Count + lane;
                    if (j >= window.Length) break;
                    var bin = m - window.Length / 2 + j;
                    var coefficient = Complex.Exp(2 * Math.PI * Complex.ImaginaryOne * bin / Length);
                    re[lane] = coefficient.Real;
                    im[lane] = coefficient.Imaginary;
                    weights[lane] = window[j];
                }
                CoefficientReal[g] = new Vector<double>(re);
                CoefficientImaginary[g] = new Vector<double>(im);
                Window[g] = new Vector<double>(weights);
            }
        }

        public Complex GetOutput()
        {
            var result = Complex.Zero;
            for (var g = 0; g < Real.Length; ++g)
            {
                var re = Window[g] * Real[g];
                var im = Window[g] * Imaginary[g];
                var count = Math.Min(Vector<double>.Count, WindowSize - g * Vector<double>.Count);
                // Preserve the scalar summation order; never include padded lanes.
                for (var lane = 0; lane < count; ++lane)
                    result += new Complex(re[lane], im[lane]);
            }
            return result / Normalization;
        }

        public void Process(double newSample)
        {
            var oldSample = Buffer.Add(newSample);
            var input = new Vector<double>(newSample - oldSample);
            for (var g = 0; g < Real.Length; ++g)
            {
                var re = Real[g];
                var im = Imaginary[g];
                Real[g] = CoefficientReal[g] * re - CoefficientImaginary[g] * im + input;
                Imaginary[g] = CoefficientReal[g] * im + CoefficientImaginary[g] * re;
            }
            if (IsEmpty(newSample, oldSample)) { Array.Clear(Real); Array.Clear(Imaginary); }
        }

        public void Process(ReadOnlyMemory<double> samples)
        {
            // Common windows use one to three groups on AVX2. Hoist their state
            // and coefficients out of the sample loop, including across ring wraps.
            switch (Real.Length)
            {
                case 1: Process1(samples); break;
                case 2: Process2(samples); break;
                case 3: Process3(samples); break;
                default: ProcessGeneral(samples); break;
            }
        }

        void Process1(ReadOnlyMemory<double> samples)
        {
            var r0 = Real[0]; var i0 = Imaginary[0];
            var a0 = CoefficientReal[0]; var b0 = CoefficientImaginary[0];
            while (!samples.IsEmpty)
            {
                var destination = Buffer.GetMemory(samples.Length).Span;
                var source = samples.Span[..destination.Length];
                for (var k = 0; k < source.Length; ++k)
                {
                    var input = new Vector<double>(source[k] - destination[k]);
                    var next0 = a0 * r0 - b0 * i0 + input;
                    i0 = a0 * i0 + b0 * r0;
                    r0 = next0;
                    if (IsEmpty(source[k], destination[k])) r0 = i0 = Vector<double>.Zero;
                }
                source.CopyTo(destination);
                samples = samples[destination.Length..];
            }
            Real[0] = r0; Imaginary[0] = i0;
        }

        void Process2(ReadOnlyMemory<double> samples)
        {
            var r0 = Real[0]; var i0 = Imaginary[0];
            var a0 = CoefficientReal[0]; var b0 = CoefficientImaginary[0];
            var r1 = Real[1]; var i1 = Imaginary[1];
            var a1 = CoefficientReal[1]; var b1 = CoefficientImaginary[1];
            while (!samples.IsEmpty)
            {
                var destination = Buffer.GetMemory(samples.Length).Span;
                var source = samples.Span[..destination.Length];
                for (var k = 0; k < source.Length; ++k)
                {
                    var input = new Vector<double>(source[k] - destination[k]);
                    var next0 = a0 * r0 - b0 * i0 + input;
                    i0 = a0 * i0 + b0 * r0;
                    r0 = next0;
                    var next1 = a1 * r1 - b1 * i1 + input;
                    i1 = a1 * i1 + b1 * r1;
                    r1 = next1;
                    if (IsEmpty(source[k], destination[k])) r0 = i0 = r1 = i1 = Vector<double>.Zero;
                }
                source.CopyTo(destination);
                samples = samples[destination.Length..];
            }
            Real[0] = r0; Imaginary[0] = i0;
            Real[1] = r1; Imaginary[1] = i1;
        }

        void Process3(ReadOnlyMemory<double> samples)
        {
            var r0 = Real[0]; var i0 = Imaginary[0];
            var a0 = CoefficientReal[0]; var b0 = CoefficientImaginary[0];
            var r1 = Real[1]; var i1 = Imaginary[1];
            var a1 = CoefficientReal[1]; var b1 = CoefficientImaginary[1];
            var r2 = Real[2]; var i2 = Imaginary[2];
            var a2 = CoefficientReal[2]; var b2 = CoefficientImaginary[2];
            while (!samples.IsEmpty)
            {
                var destination = Buffer.GetMemory(samples.Length).Span;
                var source = samples.Span[..destination.Length];
                for (var k = 0; k < source.Length; ++k)
                {
                    var input = new Vector<double>(source[k] - destination[k]);
                    var next0 = a0 * r0 - b0 * i0 + input;
                    i0 = a0 * i0 + b0 * r0;
                    r0 = next0;
                    var next1 = a1 * r1 - b1 * i1 + input;
                    i1 = a1 * i1 + b1 * r1;
                    r1 = next1;
                    var next2 = a2 * r2 - b2 * i2 + input;
                    i2 = a2 * i2 + b2 * r2;
                    r2 = next2;
                    if (IsEmpty(source[k], destination[k])) r0 = i0 = r1 = i1 = r2 = i2 = Vector<double>.Zero;
                }
                source.CopyTo(destination);
                samples = samples[destination.Length..];
            }
            Real[0] = r0; Imaginary[0] = i0;
            Real[1] = r1; Imaginary[1] = i1;
            Real[2] = r2; Imaginary[2] = i2;
        }

        void ProcessGeneral(ReadOnlyMemory<double> samples)
        {
            while (!samples.IsEmpty)
            {
                var destination = Buffer.GetMemory(samples.Length).Span;
                var source = samples.Span[..destination.Length];
                // Each group traverses the outgoing samples before overwrite.
                // Replay occupancy from the same initial count for every group.
                var initialNonzero = nonzero;
                for (var g = 0; g < Real.Length; ++g)
                {
                    nonzero = initialNonzero;
                    var re = Real[g]; var im = Imaginary[g];
                    var a = CoefficientReal[g]; var b = CoefficientImaginary[g];
                    for (var k = 0; k < source.Length; ++k)
                    {
                        var input = new Vector<double>(source[k] - destination[k]);
                        var next = a * re - b * im + input;
                        im = a * im + b * re;
                        re = next;
                        if (IsEmpty(source[k], destination[k])) re = im = Vector<double>.Zero;
                    }
                    Real[g] = re; Imaginary[g] = im;
                }
                source.CopyTo(destination);
                samples = samples[destination.Length..];
            }
        }
    }
}

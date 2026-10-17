using System;
using System.Linq;
using System.Numerics;

namespace AEther
{
    public class DFTProcessor
    {

        public static readonly double[] RectWindow = new[] { 1.0 };
        public static readonly double[] HannWindow = new[] { 0.5, 0.5 };
        // Use the exact coefficient variants for Hamming and Blackman.
        public static readonly double[] HammingWindow = new[] { 25 / 46.0, 21 / 46.0 };
        public static readonly double[] BlackmanWindow = new[] { 3969 / 9304.0, 4620 / 9304.0, 715 / 9304.0 };
        // Nuttall4b: continuous first derivative at the window boundary.
        public static readonly double[] NuttallWindow = new[] { .355768, .487396, .144232, .012604 };
        // Five-term flat top, as used by scipy.signal.windows.flattop.
        public static readonly double[] FlatTopWindow = new[] { .21557895, .41663158, .277263158, .083578947, .006947368 };

        public static double[] GetWindowCosines(DFTWindow window) => window switch
        {
            DFTWindow.Hann => HannWindow,
            DFTWindow.Rectangular => RectWindow,
            DFTWindow.Hamming => HammingWindow,
            DFTWindow.Blackman => BlackmanWindow,
            DFTWindow.Nuttall => NuttallWindow,
            DFTWindow.FlatTop => FlatTopWindow,
            _ => throw new ArgumentOutOfRangeException(nameof(window)),
        };

        readonly IDFTFilter[] Filters;
        public DFTProcessor(Domain domain, double sampleRate, bool useSIMD,
            DFTWindow windowFunction = DFTWindow.Blackman)
        {
            // Window choice is a signal-processing decision, independent of CPU vector width.
            var cosines = GetWindowCosines(windowFunction);
            var window = CreateWindow(cosines);

            if (cosines.Length == 1)
            {
                Filters = domain
                    .Select(f => new DFTFilter(f, domain.Resolution, sampleRate))
                    .ToArray();
            }
            else if (useSIMD && Vector.IsHardwareAccelerated)
            {
                Filters = domain
                    .Select(f => new WindowedDFTFilterSIMD(f, domain.Resolution, sampleRate, window))
                    .ToArray();
            }
            else
            {
                Filters = domain
                    .Select(f => new WindowedDFTFilter(f, domain.Resolution, sampleRate, window))
                    .ToArray();
            }


        }

        public static double GetAWeighting(double f)
        {
            var f2 = f * f;
            var n = Math.Pow(12194.0, 2) * f2 * f2;
            var d1 = f2 + Math.Pow(20.6, 2);
            var d2 = f2 + Math.Pow(107.7, 2);
            var d3 = f2 + Math.Pow(737.9, 2);
            var d4 = f2 + Math.Pow(12194.0, 2);
            var r = n / (d1 * Math.Sqrt(d2 * d3) * d4);
            return r;
        }

        public static double[] CreateWindow(double[] cosines)
        {
            var window = new double[2 * cosines.Length - 1];
            window[cosines.Length - 1] = cosines[0];
            for (int j = 1; j < cosines.Length; ++j)
            {
                var coeff = 0.5 * Math.Pow(-1, j) * cosines[j];
                window[cosines.Length - 1 - j] = window[cosines.Length - 1 + j] = coeff;
            }
            return window;
        }

        internal void ProcessSampleLinear(double sample, Span<double> destination)
        {
            for (var k = 0; k < Filters.Length; ++k)
            {
                Filters[k].Process(sample);
                destination[k] = 2 * Filters[k].GetOutput().Magnitude;
            }
        }

        public void Output(Memory<double> dst)
        {
            for (int k = 0; k < Filters.Length; ++k)
            {
                var filter = Filters[k];
                var bin = 2 * filter.GetOutput().Magnitude;
                dst.Span[k] = Math.Log10(Math.Max(1e-10, bin));
            }
        }

        public void Process(ReadOnlyMemory<double> samples)
        {

            foreach (var filter in Filters)
                filter.Process(samples);

        }

    }
}

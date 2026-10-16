using System;

namespace AEther
{
    /// <summary>Independent robust amplitude evidence along time, frequency, and both.
    /// Optionally estimates directional evidence from signed deviations above the rectangular background.</summary>
    public sealed class Splitter
    {
        /// <summary>Interleaved [gained S, gained T, gained N, raw amplitude]; S/T optionally estimated from signed background deviations.</summary>
        public const int OutputStride = 4;
        readonly SlidingMedian[] Horizontal, Rectangular;
        readonly double[] Magnitude, Background, Directional, Scratch;
        readonly bool SubtractBackground;
        readonly int[] First, NeighborhoodWidth;
        readonly int Width;
        readonly EvidenceOutput Presentation;

        static int Odd(double count) => Math.Max(1, (int)Math.Min(65535, Math.Round(count))) | 1;

        public Splitter(Domain domain, AnalyzerOptions options, double? analysisRate = null)
        {
            var rate = analysisRate ?? options.TimeResolution;
            if (!double.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(analysisRate));
            options.ValidateSplitter(domain.Resolution, rate);
            if (domain.Length <= 0) throw new ArgumentOutOfRangeException(nameof(domain));
            Width = Math.Min(Odd(options.MedianWidth * domain.Resolution), domain.Length);
            var length = Odd(options.MedianLength * rate);
            Presentation = new EvidenceOutput(options);
            SubtractBackground = options.SubtractNoiseBackground;
            Horizontal = new SlidingMedian[domain.Length];
            Rectangular = new SlidingMedian[domain.Length];
            First = new int[domain.Length];
            NeighborhoodWidth = new int[domain.Length];
            for (var k = 0; k < domain.Length; ++k)
            {
                First[k] = Math.Max(0, k - Width / 2);
                NeighborhoodWidth[k] = Math.Min(domain.Length - 1, k + Width / 2) - First[k] + 1;
                Horizontal[k] = new(length);
                // Each row contributes this bin's entire frequency neighborhood. Expiring
                // that many samples per row retains exactly the full time-frequency rectangle.
                Rectangular[k] = new(checked(length * NeighborhoodWidth[k]));
            }
            Magnitude = new double[domain.Length];
            Background = new double[domain.Length];
            Directional = new double[domain.Length];
            Scratch = new double[Width];
        }

        // Lower median for even populations. Three-way partition handles flat spectra in O(n).
        internal static double Median(Span<double> values)
        {
            var rank = (values.Length - 1) / 2;
            int first = 0, last = values.Length - 1;
            while (first < last)
            {
                var pivot = values[first + (last - first) / 2];
                int low = first, i = first, high = last;
                while (i <= high)
                {
                    if (values[i] < pivot)
                    {
                        (values[low], values[i]) = (values[i], values[low]); low++; i++;
                    }
                    else if (values[i] > pivot)
                    {
                        (values[i], values[high]) = (values[high], values[i]); high--;
                    }
                    else i++;
                }
                if (rank < low) last = low - 1;
                else if (rank > high) first = high + 1;
                else return pivot;
            }
            return values[first];
        }

        /// <summary>Input is log10 magnitude. All four outputs are linear amplitude.
        /// S/T are positive displayed directional evidence; N is the raw-amplitude rectangular median.
        /// Raw amplitude replaces residual. Invalid/overflowing input is zero; finite history may decay.</summary>
        public void Process(ReadOnlyMemory<double> input, Memory<double> output)
        {
            if (input.Length != Horizontal.Length || output.Length < OutputStride * Horizontal.Length)
                throw new ArgumentException("Spectrum dimensions do not match the splitter.");
            var magnitude = Magnitude;
            for (var k = 0; k < magnitude.Length; ++k)
            {
                var value = double.IsFinite(input.Span[k]) ? Math.Pow(10, input.Span[k]) : 0;
                magnitude[k] = double.IsFinite(value) ? value : 0;

            }
            // Complete the raw-amplitude background for every frequency before
            // any directional neighborhood reads deviations from adjacent bins.
            for (var k = 0; k < magnitude.Length; ++k)
            {
                var rectangle = Rectangular[k];
                var end = First[k] + NeighborhoodWidth[k];
                for (var q = First[k]; q < end; ++q) rectangle.Add(magnitude[q]);
                Background[k] = rectangle.Median;
                Directional[k] = SubtractBackground ? magnitude[k] - Background[k] : magnitude[k];
                // Store the signed deviation at arrival time. Do not recompute
                // old deviations using the current background or clip them first.
                Horizontal[k].Add(Directional[k]);
            }
            for (var k = 0; k < magnitude.Length; ++k)
            {
                var width = NeighborhoodWidth[k];
                Directional.AsSpan(First[k], width).CopyTo(Scratch);
                var frequency = Median(Scratch.AsSpan(0, width));
                var y = output.Span.Slice(OutputStride * k, OutputStride);
                Presentation.Write(Horizontal[k].Median, frequency, Background[k], magnitude[k], y);
            }
        }
    }
}

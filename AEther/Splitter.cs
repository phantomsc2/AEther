using System;
using System.Collections.Generic;

namespace AEther
{
    /// <summary>Independent robust amplitude evidence along time, frequency, and both.
    /// Evidence can overlap; no masks, cross-channel normalization, or reconstruction constraint.</summary>
    public sealed class Splitter
    {
        /// <summary>Interleaved [gained time evidence, gained frequency evidence, gained 2D evidence, raw amplitude].</summary>
        public const int OutputStride = 4;
        readonly MovingMedianHeap<double>[] Horizontal;
        readonly double[][] History;
        readonly double[] Scratch;
        readonly int Width;
        readonly double SineGain, TransientGain, NoiseGain;
        int Head = -1, Count;

        static int Odd(double count) => Math.Max(1, (int)Math.Min(65535, Math.Round(count))) | 1;

        public Splitter(Domain domain, AnalyzerOptions options, double? analysisRate = null)
        {
            var rate = analysisRate ?? options.TimeResolution;
            if (!double.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(analysisRate));
            options.ValidateSplitter(domain.Resolution, rate);
            if (domain.Length <= 0) throw new ArgumentOutOfRangeException(nameof(domain));
            Width = Math.Min(Odd(options.MedianWidth * domain.Resolution), domain.Length);
            var length = Odd(options.MedianLength * rate);
            SineGain = options.SineGain;
            TransientGain = options.TransientGain;
            NoiseGain = options.NoiseGain;
            Horizontal = new MovingMedianHeap<double>[domain.Length];
            for (var k = 0; k < domain.Length; ++k)
                Horizontal[k] = new(0, length, Comparer<double>.Default);
            History = new double[length][];
            for (var i = 0; i < length; i++) History[i] = new double[domain.Length];
            Scratch = new double[checked(length * Width)];
        }

        // Lower median for even populations. Three-way partition handles flat spectra in O(n).
        static double Median(Span<double> values)
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
        /// The first three are independent robust estimates, not probabilities or exclusive sources.
        /// Raw amplitude replaces residual. Invalid/overflowing input is zero; finite history may decay.</summary>
        public void Process(ReadOnlyMemory<double> input, Memory<double> output)
        {
            if (input.Length != Horizontal.Length || output.Length < OutputStride * Horizontal.Length)
                throw new ArgumentException("Spectrum dimensions do not match the splitter.");
            Head = (Head + 1) % History.Length;
            var magnitude = History[Head];
            for (var k = 0; k < magnitude.Length; ++k)
            {
                var value = double.IsFinite(input.Span[k]) ? Math.Pow(10, input.Span[k]) : 0;
                magnitude[k] = double.IsFinite(value) ? value : 0;
                if (Count == 0) Horizontal[k].Clear(magnitude[k]); else Horizontal[k].Filter(magnitude[k]);
            }
            Count = Math.Min(Count + 1, History.Length);
            for (var k = 0; k < magnitude.Length; ++k)
            {
                var first = Math.Max(0, k - Width / 2);
                var width = Math.Min(magnitude.Length - 1, k + Width / 2) - first + 1;
                magnitude.AsSpan(first, width).CopyTo(Scratch);
                var frequency = Median(Scratch.AsSpan(0, width));
                for (var age = 0; age < Count; age++)
                    History[(Head - age + History.Length) % History.Length].AsSpan(first, width)
                        .CopyTo(Scratch.AsSpan(age * width, width));
                var both = Median(Scratch.AsSpan(0, Count * width));
                var y = output.Span.Slice(OutputStride * k, OutputStride);
                y[0] = SineGain * Horizontal[k].State;
                y[1] = TransientGain * frequency;
                y[2] = NoiseGain * both;
                y[3] = magnitude[k];
            }
        }
    }
}

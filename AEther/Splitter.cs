using System;
using System.Collections.Generic;

namespace AEther
{
    /// <summary>
    /// Single-stage fuzzy STN classification of spectral magnitudes. Horizontal and vertical
    /// medians supply evidence; raised-cosine masks partition the current input, not the medians.
    /// See Fierro & Välimäki, arXiv:2210.14041, Eqs. (2)-(5), (8), (23)-(24).
    /// </summary>
    public sealed class Splitter
    {
        readonly MovingMedianHeap<double>[] Horizontal;
        readonly double[] Magnitude, Scratch;
        readonly int Width;
        readonly double Lower, Upper, Gain;
        bool Started;

        static int Odd(double count, int limit = 65535)
            => Math.Max(1, (int)Math.Min(limit, Math.Round(count))) | 1;

        public Splitter(Domain domain, AnalyzerOptions options, double? analysisRate = null)
        {
            var rate = analysisRate ?? options.TimeResolution;
            if (!double.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(analysisRate));
            options.ValidateSplitter(domain.Resolution, rate);
            if (domain.Length <= 0) throw new ArgumentOutOfRangeException(nameof(domain));
            Width = Odd(options.MedianWidth * domain.Resolution, domain.Length);
            Lower = options.FuzzyLower;
            Upper = options.FuzzyUpper;
            Gain = options.SplitGain;
            Horizontal = new MovingMedianHeap<double>[domain.Length];
            for (var k = 0; k < domain.Length; ++k)
                Horizontal[k] = new(0, Odd(options.MedianLength * rate), Comparer<double>.Default);
            Magnitude = new double[domain.Length];
            Scratch = new double[domain.Length];
        }

        double FrequencyMedian(int k)
        {
            var first = Math.Max(0, k - Width / 2);
            var count = Math.Min(Magnitude.Length - 1, k + Width / 2) - first + 1;
            Magnitude.AsSpan(first, count).CopyTo(Scratch);
            Scratch.AsSpan(0, count).Sort();
            return count % 2 == 0
                ? .5 * Scratch[count / 2 - 1] + .5 * Scratch[count / 2]
                : Scratch[count / 2];
        }

        double Mask(double evidence)
        {
            if (evidence <= Lower) return 0;
            if (evidence >= Upper) return 1;
            var sine = Math.Sin(.5 * Math.PI * (evidence - Lower) / (Upper - Lower));
            return sine * sine;
        }

        /// <summary>
        /// Input is log10 magnitude from DFTProcessor. Output is interleaved linear
        /// [sines, transients, noise, total magnitude], all scaled by SplitGain; RGB sums to alpha per bin.
        /// There is no floor subtraction, normalization, clipping or post-smoothing.
        /// Nonfinite/overflowing inputs are treated as zero.
        /// </summary>
        public void Process(ReadOnlyMemory<double> input, Memory<double> output)
        {
            if (input.Length != Magnitude.Length || output.Length < 4 * Magnitude.Length)
                throw new ArgumentException("Spectrum dimensions do not match the splitter.");
            for (var k = 0; k < Magnitude.Length; ++k)
            {
                var value = double.IsFinite(input.Span[k]) ? Math.Pow(10, input.Span[k]) : 0;
                Magnitude[k] = double.IsFinite(value) ? value : 0;
            }
            for (var k = 0; k < Magnitude.Length; ++k)
            {
                var magnitude = Magnitude[k];
                // Causal history (no lookahead). Seed with the first frame rather than invented silence.
                if (!Started) Horizontal[k].Clear(magnitude); else Horizontal[k].Filter(magnitude);
                var h = Horizontal[k].State;
                var v = FrequencyMedian(k);
                // Scale before division to avoid overflow; no epsilon that would bias quiet signals.
                var scale = Math.Max(h, v);
                var tonalness = scale > 0 ? (h / scale) / (h / scale + v / scale) : .5;
                var s = Mask(tonalness);
                var t = Mask(1 - tonalness);
                var n = 1 - s - t;
                var scaledMagnitude = Gain * magnitude;
                var y = output.Span.Slice(4 * k, 4);
                y[0] = s * scaledMagnitude;
                y[1] = t * scaledMagnitude;
                y[2] = n * scaledMagnitude;
                y[3] = scaledMagnitude;
            }
            Started = true;
        }
    }
}

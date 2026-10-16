using System;
using System.Collections.Generic;

namespace AEther
{
    /// <summary>
    /// Fuzzy STN classification followed by causal bilateral STN+R decomposition. Horizontal and vertical
    /// medians supply evidence; raised-cosine masks partition the current input, not the medians.
    /// See Fierro & Välimäki, arXiv:2210.14041, Eqs. (2)-(5), (8), (23)-(24).
    /// </summary>
    public sealed class Splitter
    {
        /// <summary>Interleaved [gained S, gained T, gained N, original magnitude, signed ungained residual].</summary>
        public const int OutputStride = 5;
        readonly BilateralSmoother Smoother;
        readonly double[] Classified, Smoothed;
        readonly MovingMedianHeap<double>[] Horizontal;
        readonly double[] Magnitude, Scratch;
        readonly int Width;
        readonly double Lower, Upper, SineGain, TransientGain, NoiseGain;
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
            SineGain = options.SineGain;
            TransientGain = options.TransientGain;
            NoiseGain = options.NoiseGain;
            Horizontal = new MovingMedianHeap<double>[domain.Length];
            for (var k = 0; k < domain.Length; ++k)
                Horizontal[k] = new(0, Odd(options.MedianLength * rate), Comparer<double>.Default);
            Smoother = new(domain.Length, rate, domain.Resolution, options);
            Classified = new double[3 * domain.Length];
            Smoothed = new double[3 * domain.Length];
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
        /// [gained sines, gained transients, gained noise, original magnitude, signed residual].
        /// Before class gains, S + T + N + R equals original magnitude. Residual is never gained.
        /// Bilateral smoothing averages linear magnitudes with log-amplitude edge weights.
        /// Nonfinite/overflowing inputs are treated as zero.
        /// </summary>
        public void Process(ReadOnlyMemory<double> input, Memory<double> output)
        {
            if (input.Length != Magnitude.Length || output.Length < OutputStride * Magnitude.Length)
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
                Classified[3 * k] = s * magnitude;
                Classified[3 * k + 1] = t * magnitude;
                Classified[3 * k + 2] = n * magnitude;
            }
            Smoother.Process(Classified, Smoothed);
            for (var k = 0; k < Magnitude.Length; ++k)
            {
                var y = output.Span.Slice(OutputStride * k, OutputStride);
                // Exact silence is an absolute anchor, even with active history.
                var s = Magnitude[k] > 0 ? Smoothed[3 * k] : 0;
                var t = Magnitude[k] > 0 ? Smoothed[3 * k + 1] : 0;
                var n = Magnitude[k] > 0 ? Smoothed[3 * k + 2] : 0;
                y[0] = SineGain * s;
                y[1] = TransientGain * t;
                y[2] = NoiseGain * n;
                y[3] = Magnitude[k];
                y[4] = ((Magnitude[k] - s) - t) - n;
            }
            Started = true;
        }
    }
}

using System;
using System.Collections.Generic;

namespace AEther
{
    /// <summary>Independent median windows for sustained ridges, broadband attacks and smooth noise.</summary>
    public sealed class Splitter
    {
        readonly MovingMedianHeap<double>[] Time;
        readonly MovingMedianHeap<double>[] RidgeTime, NoiseTime;
        readonly double[] Level, SmoothTime, SpikeTime, Noise, Transient, Scratch, Residual, VisibleDb;
        readonly double[] PostSinuoid, PostTransient, PostNoise;
        readonly double PostDecay;
        readonly int SinuoidWidth, TransientWidth, NoiseWidth;
        readonly double Floor, FloorPower;
        readonly bool Adaptive;
        readonly SplitterOrder Order;
        bool Started;
        public SpectralRange Range { get; }
        public ReadOnlyMemory<double> ResidualDb => Residual;

        static int Odd(double count, int limit = 65535)
            => Math.Max(1, (int)Math.Min(limit, Math.Round(count))) | 1;

        public Splitter(Domain domain, AnalyzerOptions options, double? analysisRate = null)
        {
            var rate = analysisRate ?? options.TimeResolution;
            if (!double.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(analysisRate));
            Floor = options.SeparationFloorDb;
            if (!double.IsFinite(options.JitterLength) || options.JitterLength < 0)
                throw new ArgumentOutOfRangeException(nameof(options.JitterLength));
            PostDecay = options.JitterLength > 0 ? Math.Exp(-1 / (rate * options.JitterLength)) : 0;
            FloorPower = Math.Pow(10, Floor / 10);
            Adaptive = options.AdaptiveSpectrumRange;
            Order = options.SplitOrder;
            Range = new(options.QuantileHistorySeconds, 1 / rate, Floor);
            SinuoidWidth = Odd(options.SinuoidWidth * domain.Resolution, domain.Length);
            TransientWidth = Odd(options.TransientWidth * domain.Resolution, domain.Length);
            NoiseWidth = Odd(options.NoiseWidth * domain.Resolution, domain.Length);
            Time = new MovingMedianHeap<double>[domain.Length];
            RidgeTime = new MovingMedianHeap<double>[domain.Length];
            NoiseTime = new MovingMedianHeap<double>[domain.Length];
            for (var k = 0; k < domain.Length; ++k)
            {
                Time[k] = new(0, Odd(options.TransientLength * rate), Comparer<double>.Default);
                RidgeTime[k] = new(0, Odd(options.SinuoidLength * rate), Comparer<double>.Default);
                NoiseTime[k] = new(0, Odd(options.NoiseLength * rate), Comparer<double>.Default);
            }
            Level = new double[domain.Length]; SmoothTime = new double[domain.Length];
            SpikeTime = new double[domain.Length]; Noise = new double[domain.Length];
            Transient = new double[domain.Length]; Scratch = new double[domain.Length];
            Residual = new double[domain.Length]; VisibleDb = new double[domain.Length];
            PostSinuoid = new double[domain.Length]; PostTransient = new double[domain.Length];
            PostNoise = new double[domain.Length];
        }

        void FrequencyMedian(double[] source, double[] destination, int width)
        {
            for (var k = 0; k < source.Length; ++k)
            {
                var first = Math.Max(0, k - width / 2);
                var count = Math.Min(source.Length - 1, k + width / 2) - first + 1;
                source.AsSpan(first, count).CopyTo(Scratch);
                Scratch.AsSpan(0, count).Sort();
                var median = count % 2 == 0
                    ? .5 * (Scratch[count / 2 - 1] + Scratch[count / 2]) : Scratch[count / 2];
                destination[k] = median;
            }
        }

        void ApplyBranch(double[] destination, MovingMedianHeap<double>[] time, int width, bool timeSpike, bool frequencySpike)
        {
            if (Order == SplitterOrder.TimeThenFrequency)
            {
                TimeFilter(Level, SpikeTime, time, timeSpike);
                FrequencyMedian(SpikeTime, destination, width);
                if (frequencySpike)
                    for (var k = 0; k < Level.Length; ++k)
                        destination[k] = Math.Max(0, SpikeTime[k] - destination[k]);
            }
            else
            {
                FrequencyMedian(Level, SpikeTime, width);
                if (frequencySpike)
                    for (var k = 0; k < Level.Length; ++k)
                        SpikeTime[k] = Math.Max(0, Level[k] - SpikeTime[k]);
                TimeFilter(SpikeTime, destination, time, timeSpike);
            }
        }

        void TimeFilter(double[] source, double[] destination, MovingMedianHeap<double>[] time, bool spike)
        {
            for (var k = 0; k < Level.Length; ++k)
            {
                if (!Started) time[k].Clear(source[k]); else time[k].Filter(source[k]);
                destination[k] = spike ? Math.Max(0, source[k] - time[k].State) : time[k].State;
            }
        }
        public void Process(ReadOnlyMemory<double> input, Memory<double> output)
        {
            if (input.Length != Level.Length || output.Length < 4 * Level.Length)
                throw new ArgumentException("Spectrum dimensions do not match the splitter.");
            for (var k = 0; k < Level.Length; ++k)
            {
                var raw = double.IsFinite(input.Span[k]) ? Math.Clamp(20 * input.Span[k], -200, 20) : -200;
                // Differences must be taken in an additive energy domain.
                // Subtracting dB allocates the whole floor-to-bed range to noise.
                Level[k] = Math.Max(0, Math.Pow(10, raw / 10) - FloorPower);
            }
            ApplyBranch(SmoothTime, RidgeTime, SinuoidWidth, false, true);
            ApplyBranch(Transient, Time, TransientWidth, true, false);
            ApplyBranch(Noise, NoiseTime, NoiseWidth, false, false);
            for (var k = 0; k < Level.Length; ++k)
                Residual[k] = Math.Max(0, Level[k] - SmoothTime[k] - Transient[k] - Noise[k]);
            for (var k = 0; k < Level.Length; ++k)
            {
                // Median structure is untouched. Smooth its power outputs before
                // common normalization; transient rises retain their full peak.
                PostSinuoid[k] = Started ? PostDecay * PostSinuoid[k] + (1 - PostDecay) * SmoothTime[k] : SmoothTime[k];
                PostNoise[k] = Started ? PostDecay * PostNoise[k] + (1 - PostDecay) * Noise[k] : Noise[k];
                PostTransient[k] = Math.Max(Transient[k], PostDecay * PostTransient[k]);
                SmoothTime[k] = PostSinuoid[k]; Noise[k] = PostNoise[k]; Transient[k] = PostTransient[k];
                var retained = SmoothTime[k] + Noise[k] + Transient[k];
                VisibleDb[k] = 10 * Math.Log10(FloorPower + retained);
                // Diagnostic dB above the floor; the working components are power.
                Residual[k] = 10 * Math.Log10(1 + Residual[k] / FloorPower);
            }
            if (Adaptive) Range.Process(VisibleDb);
            // A common brightness and physical amplitude shares preserve mixture
            // proportions. The learned floor is diagnostic, not subtracted from
            // noise: a pure steady broadband passage must remain visible.
            var span = Adaptive ? Math.Max(Range.SpanDb, Range.CeilingDb - Floor) : 100;
            for (var k = 0; k < Level.Length; ++k)
            {
                var retained = SmoothTime[k] + Noise[k] + Transient[k];
                var brightness = Math.Clamp((VisibleDb[k] - Floor) / span, 0, 1);
                var y = output.Span.Slice(4 * k, 4);
                y[0] = retained > 0 ? brightness * Math.Sqrt(SmoothTime[k] / retained) : 0;
                y[1] = retained > 0 ? brightness * Math.Sqrt(Transient[k] / retained) : 0;
                y[2] = retained > 0 ? brightness * Math.Sqrt(Noise[k] / retained) : 0;
                // Combined retained absolute level; residual never enters a texture.
                y[3] = VisibleDb[k] > Floor ? Math.Clamp(1 + VisibleDb[k] / 100, 0, 1) : 0;
            }
            Started = true;
        }
    }
}

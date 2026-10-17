using System;

namespace AEther;

/// <summary>Rolling Gaussian fit with time-linear S and log-frequency-linear T.
/// Shared moments update the complete local rectangles without rebuilding patches.
/// Profiles use zero-mean centering; residual scale is RMS, not MAD.</summary>
public sealed class LocalJointSplitter
{
    const double Epsilon = 2.220446049250313e-16;
    readonly int Bands, Length;
    readonly bool TimeSlope, FrequencySlope;
    readonly double Rate, Resolution;
    readonly int[] First, End;
    readonly double[] History, RowHistory, TiltHistory, Magnitude, Current, RowMeans, RowTilts, TimeTrends;
    readonly Moment[] Columns, Rows, Tilts;
    readonly Sum[] TimeCross;
    readonly JointEvidence[] Results;
    readonly EvidenceOutput Presentation;
    int Next, Count, RebaseIndex;
    double Unit = 1;

    /// <summary>Signed, ungained effects and residual RMS. Valid until the next Process call.</summary>
    public ReadOnlyMemory<JointEvidence> Estimates => Results;

    static int Odd(double count) => Math.Max(1, (int)Math.Min(65535, Math.Round(count))) | 1;

    public LocalJointSplitter(Domain domain, AnalyzerOptions options, double? analysisRate = null)
    {
        var rate = analysisRate ?? options.TimeResolution;
        if (!double.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(analysisRate));
        options.ValidateSplitter(domain.Resolution, rate);
        if (domain.Length <= 0) throw new ArgumentOutOfRangeException(nameof(domain));
        Bands = domain.Length;
        Rate = rate; Resolution = domain.Resolution;
        TimeSlope = options.JointTimeSlopeEnabled;
        FrequencySlope = options.JointFrequencySlopeEnabled;
        Length = Odd(options.MedianLength * rate);
        var width = Math.Min(Odd(options.MedianWidth * domain.Resolution), Bands);
        First = new int[Bands]; End = new int[Bands];
        for (var k = 0; k < Bands; ++k)
        {
            First[k] = Math.Max(0, k - width / 2);
            End[k] = Math.Min(Bands, k + width / 2 + 1);
        }
        History = new double[checked(Bands * Length)];
        RowHistory = new double[History.Length];
        TiltHistory = FrequencySlope ? new double[History.Length] : Array.Empty<double>();
        Magnitude = new double[Bands]; Current = new double[Bands]; RowMeans = new double[Bands];
        RowTilts = new double[Bands]; TimeTrends = new double[Bands];
        TimeCross = new Sum[Bands];
        Columns = new Moment[Bands]; Rows = new Moment[Bands]; Tilts = new Moment[Bands];
        Results = new JointEvidence[Bands];
        Presentation = new EvidenceOutput(options);
    }

    public void Process(ReadOnlyMemory<double> input, Memory<double> output)
    {
        if (input.Length != Bands || output.Length < Splitter.OutputStride * Bands)
            throw new ArgumentException("Spectrum dimensions do not match the joint splitter.");
        double maximum = 0;
        for (var k = 0; k < Bands; ++k)
        {
            var value = double.IsFinite(input.Span[k]) ? Math.Pow(10, input.Span[k]) : 0;
            Magnitude[k] = double.IsFinite(value) ? value : 0;
            maximum = Math.Max(maximum, Magnitude[k]);
        }
        // Ordinary audio stays in one unit system. Exceptional magnitudes are
        // rescaled before squaring so finite input cannot overflow the moments.
        if (maximum > Unit || Count == 0 && maximum > 0)
        {
            var nextUnit = Math.ScaleB(1, Math.Clamp(Math.ILogB(maximum) + 2, -1074, 1023));
            nextUnit = Math.Max(nextUnit, maximum);
            Rescale(Unit / nextUnit);
            Unit = nextUnit;
        }
        var offset = Next * Bands;
        for (var k = 0; k < Bands; ++k)
        {
            var value = Magnitude[k] / Unit;
            Current[k] = value;
            var oldValue = History[offset + k];
            if (TimeSlope)
            {
                // Cross moment against centered chronological sample indices.
                // When the window advances, all retained coordinates shift by -1.
                TimeCross[k].Add(Count < Length
                    ? .5 * Count * (value - Columns[k].Mean)
                    : .5 * (Count - 1) * (value - oldValue) - Count * (Columns[k].Mean - oldValue));
            }
            Columns[k].Update(oldValue, value, Count, Length);
            if (Columns[k].Nonzero == 0) TimeCross[k] = default;
            History[offset + k] = value;
        }
        NeighborhoodMeans();
        for (var k = 0; k < Bands; ++k)
        {
            Rows[k].Update(RowHistory[offset + k], RowMeans[k], Count, Length);
            RowHistory[offset + k] = RowMeans[k];
            if (FrequencySlope)
            {
                Tilts[k].Update(TiltHistory[offset + k], RowTilts[k], Count, Length);
                TiltHistory[offset + k] = RowTilts[k];
            }
        }
        Count = Math.Min(Count + 1, Length);
        Next = (Next + 1) % Length;
        // Rebuild one column and its neighborhood mean/tilt histories per row. This bounds
        // long-run downdate drift at O(Length) extra work, not O(Bands*Length*Width).
        Rebase(RebaseIndex);
        RebaseIndex = (RebaseIndex + 1) % Bands;

        var timeFactor = TimeSlope && Count > 1 ? 6.0 / (Count * ((double)Count + 1)) : 0;
        var timeVariance = TimeSlope && Count > 1 ? (Count + 1.0) / (3 * (Count - 1)) : 0;
        for (var k = 0; k < Bands; ++k)
            TimeTrends[k] = TimeCross[k].Value * timeFactor;
        Sum means = default, moments = default, trends = default, trendSquares = default, trendPositions = default;
        int left = 0, right = 0, active = 0;
        for (var k = 0; k < Bands; ++k)
        {
            while (right < End[k])
            {
                means.Add(Columns[right].Mean); moments.Add(Columns[right].M2);
                trends.Add(TimeTrends[right]); trendSquares.Add(TimeTrends[right] * TimeTrends[right]);
                trendPositions.Add(right * TimeTrends[right]);
                if (Columns[right++].Nonzero > 0) active++;
            }
            while (left < First[k])
            {
                means.Add(-Columns[left].Mean); moments.Add(-Columns[left].M2);
                trends.Add(-TimeTrends[left]); trendSquares.Add(-TimeTrends[left] * TimeTrends[left]);
                trendPositions.Add(-left * TimeTrends[left]);
                if (Columns[left++].Nonzero > 0) active--;
            }
            var width = End[k] - First[k];
            var frequencyVariance = FrequencySlope && width > 1 ? (width + 1.0) / (3 * (width - 1)) : 0;
            var center = .5 * (First[k] + End[k] - 1);
            var x = FrequencySlope && width > 1 ? 2 * (k - center) / (width - 1) : 0;
            var averageTrend = trends.Value / width;
            var interaction = TimeSlope && FrequencySlope && Count > 1 && width > 1
                ? 6 * (trendPositions.Value - center * trends.Value) / (width * (width + 1.0)) : 0;
            double background, sine, transient, rms, sineSlope = 0, transientSlope = 0;
            if (active == 0)
            {
                means = default; moments = default;
                trends = default; trendSquares = default; trendPositions = default;
                background = sine = transient = rms = 0;
            }
            else
            {
                background = means.Value / width;
                // Each directional field has zero average across its other axis.
                // The shared bilinear x*u component is assigned half to each field.
                sineSlope = TimeSlope ? TimeTrends[k] - averageTrend - .5 * interaction * x : 0;
                transientSlope = FrequencySlope ? RowTilts[k] - Tilts[k].Mean - .5 * interaction : 0;
                sine = Columns[k].Mean - background + sineSlope;
                transient = RowMeans[k] - background + transientSlope * x;
                // ANOVA identity: residual variance = mean temporal variance
                // across frequency bins minus temporal variance of their mean.
                var total = Math.Max(0, moments.Value / (width * Count));
                var explained = Math.Max(0, Rows[k].M2 / Count);
                var timeExplained = timeVariance * Math.Max(0, trendSquares.Value / width - averageTrend * averageTrend);
                var frequencyExplained = frequencyVariance * Tilts[k].M2 / Count;
                var overlap = interaction * interaction * timeVariance * frequencyVariance;
                // Orthogonal projection onto both directional regression spaces:
                // subtract each extra projection and restore their shared overlap.
                var variance = total - explained - timeExplained - frequencyExplained + overlap;
                var roundoff = 512 * Epsilon * (total + explained + timeExplained + frequencyExplained + overlap);
                if ((width == 1 || FrequencySlope && width == 2)
                    || (Count == 1 || TimeSlope && Count == 2)) variance = 0;
                else if (variance < -roundoff)
                    variance = DirectResidualVariance(k, background, averageTrend, interaction);
                else if (variance <= roundoff) variance = 0;
                rms = Math.Sqrt(Math.Max(0, variance));
                var effectRoundoff = 64 * Epsilon * Math.Max(Math.Abs(background), Math.Abs(RowMeans[k]));
                if (Math.Abs(sine) <= effectRoundoff) sine = 0;
                if (Math.Abs(transient) <= effectRoundoff) transient = 0;
            }
            Results[k] = new JointEvidence(sine * Unit, transient * Unit, Math.Max(0, background) * Unit,
                rms * Unit, Magnitude[k], width, Count)
            {
                SineTimeSlope = Count > 1 ? sineSlope * Unit * (2 * Rate / (Count - 1)) : 0,
                TransientFrequencySlope = width > 1 ? transientSlope * Unit * (2 * Resolution / (width - 1)) : 0,
                TimeBasisRank = TimeSlope && Count > 1 ? 2 : 1,
                FrequencyBasisRank = FrequencySlope && width > 1 ? 2 : 1,
            };
            Presentation.Write(Results[k].Sine, Results[k].Transient, Results[k].Background,
                Magnitude[k], output.Span.Slice(k * Splitter.OutputStride, Splitter.OutputStride));
        }
    }

    void NeighborhoodMeans()
    {
        Sum sum = default, positions = default;
        int left = 0, right = 0, active = 0;
        for (var k = 0; k < Bands; ++k)
        {
            while (right < End[k])
            {
                sum.Add(Current[right]);
                positions.Add(right * Current[right]);
                if (Current[right++] > 0) active++;
            }
            while (left < First[k])
            {
                sum.Add(-Current[left]);
                positions.Add(-left * Current[left]);
                if (Current[left++] > 0) active--;
            }
            if (active == 0) { sum = default; positions = default; }
            var width = End[k] - First[k];
            RowMeans[k] = active == 0 ? 0 : Math.Max(0, sum.Value / width);
            RowTilts[k] = FrequencySlope && active > 0 && width > 1
                ? 6 * (positions.Value - .5 * (First[k] + End[k] - 1) * sum.Value) / (width * (width + 1.0)) : 0;
        }
    }

    void Rebase(int k)
    {
        Columns[k] = default; Rows[k] = default; Tilts[k] = default; TimeCross[k] = default;
        for (var t = 0; t < Count; ++t)
        {
            var row = (Next - Count + t + Length) % Length;
            Columns[k].Update(0, History[row * Bands + k], t, Length);
            Rows[k].Update(0, RowHistory[row * Bands + k], t, Length);
            if (FrequencySlope) Tilts[k].Update(0, TiltHistory[row * Bands + k], t, Length);
        }
        if (TimeSlope)
        for (var t = 0; t < Count; ++t)
        {
            var row = (Next - Count + t + Length) % Length;
            TimeCross[k].Add((t - .5 * (Count - 1)) * (History[row * Bands + k] - Columns[k].Mean));
        }
    }

    void Rescale(double factor)
    {
        if (Count == 0) return;
        for (var i = 0; i < History.Length; ++i)
        {
            History[i] *= factor; RowHistory[i] *= factor;
            if (FrequencySlope) TiltHistory[i] *= factor;
        }
        for (var k = 0; k < Bands; ++k) Rebase(k);
    }

    double DirectResidualVariance(int k, double background, double averageTrend, double interaction)
    {
        Sum squares = default;
        var width = End[k] - First[k];
        var center = .5 * (First[k] + End[k] - 1);
        for (var t = 0; t < Count; ++t)
        {
            var row = (Next - Count + t + Length) % Length;
            var u = TimeSlope && Count > 1 ? (2.0 * t - (Count - 1)) / (Count - 1) : 0;
            var tilt = FrequencySlope ? TiltHistory[row * Bands + k] - Tilts[k].Mean : 0;
            for (var f = First[k]; f < End[k]; ++f)
            {
                var x = FrequencySlope && width > 1 ? 2 * (f - center) / (width - 1) : 0;
                var prediction = Columns[f].Mean + RowHistory[row * Bands + k] - background
                    + (TimeTrends[f] - averageTrend) * u + tilt * x - interaction * x * u;
                var residual = History[row * Bands + f] - prediction;
                squares.Add(residual * residual);
            }
        }
        return squares.Value / (Count * width);
    }

    struct Sum
    {
        double Total, Correction;
        public readonly double Value => Total + Correction;
        public void Add(double value)
        {
            var next = Total + value;
            Correction += Math.Abs(Total) >= Math.Abs(value)
                ? (Total - next) + value : (value - next) + Total;
            Total = next;
        }
    }

    struct Moment
    {
        Sum Location, Squares;
        public int Nonzero;
        public readonly double Mean => Location.Value;
        public readonly double M2 => Math.Max(0, Squares.Value);

        public void Update(double oldValue, double value, int count, int capacity)
        {
            var previousMean = Mean;
            if (count < capacity)
            {
                if (value != 0) Nonzero++;
                Location.Add((value - previousMean) / (count + 1));
                Squares.Add((value - previousMean) * (value - Mean));
            }
            else
            {
                if (oldValue != 0) Nonzero--;
                if (value != 0) Nonzero++;
                var difference = value - oldValue;
                Location.Add(difference / count);
                Squares.Add(difference * ((value - Mean) + (oldValue - previousMean)));
            }
            // Exact expiry of the last nonzero observation clears numerical
            // residue, preserving silence after previously active input.
            if (Nonzero == 0) this = default;
        }
    }
}

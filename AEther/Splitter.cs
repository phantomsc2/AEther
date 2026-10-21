using System;

namespace AEther;

/// <summary>Rolling Gaussian additive fit of spectral S and temporal T.
/// Shared moments update the complete local rectangles without rebuilding patches.
/// Profiles use zero-mean centering; residual scale is RMS, not MAD.</summary>
public sealed class Splitter
{
    /// <summary>Rendered [positive S, positive T, background B, raw amplitude A]; residual statistics are separate.</summary>
    public const int OutputStride = 4;
    const double Epsilon = 2.220446049250313e-16;
    readonly int Bands, Length;
    readonly int[] First, End;
    readonly double[] RawHistory, RowMaximum, History, RowHistory, Magnitude, Current, RowMeans;
    readonly Moment[] Columns, Rows;
    readonly JointEvidence[] Results;
    int Next, Count, RebaseIndex;
    double Unit = 1;

    /// <summary>Signed effects and residual RMS in input amplitude units. Valid until the next Process/Reset call.</summary>
    public ReadOnlyMemory<JointEvidence> Estimates => Results;

    static int Odd(double count) => Math.Max(1, (int)Math.Min(65535, Math.Round(count))) | 1;

    public Splitter(Domain domain, AnalyzerOptions options, double? analysisRate = null)
    {
        var rate = analysisRate ?? options.TimeResolution;
        if (!double.IsFinite(rate) || rate <= 0) throw new ArgumentOutOfRangeException(nameof(analysisRate));
        options.ValidateSplitter(domain.Resolution, rate);
        if (domain.Length <= 0) throw new ArgumentOutOfRangeException(nameof(domain));
        Bands = domain.Length;
        Length = Odd(options.TimeWindowSeconds * rate);
        var width = Math.Min(Odd(options.FrequencyWindowOctaves * domain.Resolution), Bands);
        First = new int[Bands]; End = new int[Bands];
        for (var k = 0; k < Bands; ++k)
        {
            First[k] = Math.Max(0, k - width / 2);
            End[k] = Math.Min(Bands, k + width / 2 + 1);
        }
        History = new double[checked(Bands * Length)];
        RawHistory = new double[History.Length];
        RowMaximum = new double[Length];
        RowHistory = new double[History.Length];
        Magnitude = new double[Bands]; Current = new double[Bands]; RowMeans = new double[Bands];
        Columns = new Moment[Bands]; Rows = new Moment[Bands];
        Results = new JointEvidence[Bands];
    }

    public void Process(ReadOnlyMemory<double> input, Memory<double> output)
    {
        if (input.Length != Bands || output.Length < Splitter.OutputStride * Bands)
            throw new ArgumentException("Spectrum dimensions do not match the joint splitter.");
        for (var k = 0; k < Bands; ++k)
            Magnitude[k] = SpectralMath.FromLog(input.Span[k]);
        ProcessLinear(Magnitude, output);
    }

    /// <summary>Fit gained linear magnitudes. Nonpositive/nonfinite observations are zero.</summary>
    public void ProcessLinear(ReadOnlyMemory<double> input, Memory<double> output)
    {
        if (input.Length != Bands || output.Length < OutputStride * Bands)
            throw new ArgumentException("Spectrum dimensions do not match the joint splitter.");
        double maximum = 0;
        for (var k = 0; k < Bands; ++k)
        {
            Magnitude[k] = SpectralMath.Sanitize(input.Span[k]);
            maximum = Math.Max(maximum, Magnitude[k]);
        }
        var offset = Next * Bands;
        Magnitude.CopyTo(RawHistory, offset);
        RowMaximum[Next] = maximum;
        foreach (var peak in RowMaximum) maximum = Math.Max(maximum, peak);
        // Keep original observations so quiet values survive a temporary enormous
        // range. Rebuild bounded moments when the retained range changes materially.
        if (maximum > Unit || maximum > 0 && (Count == 0 || maximum < Math.ScaleB(Unit, -32)))
        {
            var nextUnit = Math.ScaleB(1, Math.Clamp(Math.ILogB(maximum) + 2, -1074, 1023));
            Unit = Math.Max(nextUnit, maximum);
            Count = Math.Min(Count + 1, Length);
            Next = (Next + 1) % Length;
            RebuildHistory();
        }
        else
        {
            for (var k = 0; k < Bands; ++k)
            {
                var value = Magnitude[k] / Unit;
                Current[k] = value;
                var oldValue = History[offset + k];
                Columns[k].Update(oldValue, value, Count, Length);
                History[offset + k] = value;
            }
            NeighborhoodMeans();
            for (var k = 0; k < Bands; ++k)
            {
                Rows[k].Update(RowHistory[offset + k], RowMeans[k], Count, Length);
                RowHistory[offset + k] = RowMeans[k];
            }
            Count = Math.Min(Count + 1, Length);
            Next = (Next + 1) % Length;
            // Rebuild one column and its neighborhood mean history per row. This bounds
            // long-run downdate drift at O(Length) extra work, not O(Bands*Length*Width).
            Rebase(RebaseIndex);
            RebaseIndex = (RebaseIndex + 1) % Bands;
        }

        Sum means = default, moments = default;
        int left = 0, right = 0, active = 0;
        for (var k = 0; k < Bands; ++k)
        {
            while (right < End[k])
            {
                means.Add(Columns[right].Mean); moments.Add(Columns[right].M2);
                if (Columns[right++].Nonzero > 0) active++;
            }
            while (left < First[k])
            {
                means.Add(-Columns[left].Mean); moments.Add(-Columns[left].M2);
                if (Columns[left++].Nonzero > 0) active--;
            }
            var width = End[k] - First[k];
            double background, sine, transient, rms;
            if (active == 0)
            {
                means = default; moments = default;
                background = sine = transient = rms = 0;
            }
            else
            {
                background = means.Value / width;
                sine = Columns[k].Mean - background;
                transient = RowMeans[k] - background;
                // ANOVA identity: residual variance = mean temporal variance
                // across frequency bins minus temporal variance of their mean.
                var total = Math.Max(0, moments.Value / (width * Count));
                var explained = Math.Max(0, Rows[k].M2 / Count);
                var variance = total - explained;
                var roundoff = 512 * Epsilon * (total + explained);
                if (width == 1 || Count == 1) variance = 0;
                else if (variance < -roundoff)
                    variance = DirectResidualVariance(k, background);
                else if (variance <= roundoff) variance = 0;
                rms = Math.Sqrt(Math.Max(0, variance));
                var effectRoundoff = 64 * Epsilon * Math.Max(Math.Abs(background), Math.Abs(RowMeans[k]));
                if (Math.Abs(sine) <= effectRoundoff) sine = 0;
                if (Math.Abs(transient) <= effectRoundoff) transient = 0;
            }
            Results[k] = new JointEvidence(sine * Unit, transient * Unit, Math.Max(0, background) * Unit,
                rms * Unit, Magnitude[k], width, Count);
            EvidenceOutput.Write(Results[k].Sine, Results[k].Transient, Results[k].Background,
                Magnitude[k], output.Span.Slice(k * Splitter.OutputStride, Splitter.OutputStride));
        }
    }

    /// <summary>Forget support after a discontinuity; gaps are not zero observations.</summary>
    public void Reset()
    {
        Array.Clear(History); Array.Clear(RowHistory);
        Array.Clear(RawHistory); Array.Clear(RowMaximum);
        Array.Clear(Columns); Array.Clear(Rows);
        Array.Clear(Results);
        Next = Count = RebaseIndex = 0;
        Unit = 1;
    }

    void NeighborhoodMeans()
    {
        Sum sum = default;
        int left = 0, right = 0, active = 0;
        for (var k = 0; k < Bands; ++k)
        {
            while (right < End[k])
            {
                sum.Add(Current[right]);
                if (Current[right++] > 0) active++;
            }
            while (left < First[k])
            {
                sum.Add(-Current[left]);
                if (Current[left++] > 0) active--;
            }
            if (active == 0) sum = default;
            var width = End[k] - First[k];
            RowMeans[k] = active == 0 ? 0 : Math.Max(0, sum.Value / width);
        }
    }

    void Rebase(int k)
    {
        Columns[k] = default; Rows[k] = default;
        for (var t = 0; t < Count; ++t)
        {
            var row = (Next - Count + t + Length) % Length;
            Columns[k].Update(0, History[row * Bands + k], t, Length);
            Rows[k].Update(0, RowHistory[row * Bands + k], t, Length);
        }
    }

    void RebuildHistory()
    {
        for (var t = 0; t < Count; ++t)
        {
            var offset = ((Next - Count + t + Length) % Length) * Bands;
            for (var k = 0; k < Bands; ++k)
                History[offset + k] = Current[k] = RawHistory[offset + k] / Unit;
            NeighborhoodMeans();
            for (var k = 0; k < Bands; ++k)
                RowHistory[offset + k] = RowMeans[k];
        }
        for (var k = 0; k < Bands; ++k) Rebase(k);
    }

    double DirectResidualVariance(int k, double background)
    {
        Sum squares = default;
        var width = End[k] - First[k];
        for (var t = 0; t < Count; ++t)
        {
            var row = (Next - Count + t + Length) % Length;
            for (var f = First[k]; f < End[k]; ++f)
            {
                var prediction = Columns[f].Mean + RowHistory[row * Bands + k] - background;
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

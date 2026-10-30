using System.Threading.Channels;
using System.Numerics;

namespace AEther.Vortice;

/// <summary>Owned handoff. History, aggregates and envelopes are touched only by the render thread.</summary>
public sealed class AudioHistory
{
    const double Retention = .05, HandoffSeconds = .1;
    sealed record Row(long Index, long Capture, long Completed, double Start, double End,
        double Arrival, float[] Samples, float[] Residual, float[] Diagnostics, long Generation, bool Live, AudioStreamClock? Clock, SourceWatermark? Progress);
    readonly Channel<Row> pending, offline;
    readonly Queue<Row> scheduled = new(), recent = new();
    readonly TimeProvider timeProvider;
    readonly long started;
    readonly double rate;
    readonly int handoffRows;
    readonly double[] sum, residualSum, maximum, residualMaximum, diagnosticSum, diagnosticMaximum;
    long newestRow = -1, consumedRow = -1, firstRow, generation = -1;
    double position, newestEnd, newestArrival, cursor, stateTime;
    double historyClockTime;
    double historyClockRow;
    double aggregateStart;
    double appliedSpectrumAggregation = .5, spectrumAggregation = .5;
    AudioStreamClock? streamClock;
    bool haveHistoryClock;
    bool haveCursor, haveState, haveConsumed, live;
    long dropped;
    int dirtyStart, dirtyCount;
    bool historyCleared;

    internal readonly record struct HistoryRange(int Start, int Count);

    /// <summary>Call after directly editing History, on the render thread before rendering.</summary>
    public void InvalidateHistory() { dirtyStart = 0; dirtyCount = Rows; }

    internal int GetHistoryUpdates(Span<HistoryRange> ranges)
    {
        if (dirtyCount == 0) return 0;
        int count = Math.Min(dirtyCount, Rows - dirtyStart);
        ranges[0] = new(dirtyStart, count);
        if (count == dirtyCount) return 1;
        ranges[1] = new(0, dirtyCount - count);
        return 2;
    }

    internal void AcknowledgeHistoryUpload() => dirtyCount = 0;

    void MarkHistoryRows(int start, int count)
    {
        if (count >= Rows) { InvalidateHistory(); return; }
        if (dirtyCount == Rows) return;
        if (dirtyCount == 0) { dirtyStart = start; dirtyCount = count; return; }
        // Accepted rows advance monotonically. Their writes and cleared gaps extend
        // one circular interval until every physical row has been touched.
        int distance = (start - dirtyStart + Rows) % Rows;
        dirtyCount = Math.Min(Rows, Math.Max(dirtyCount, distance + count));
        if (dirtyCount == Rows) dirtyStart = 0;
    }

    public PipelineProfile? Profile { get; set; }
    public PipelineProgress? Progress { get; set; }
    public double PresentationDelay { get; }
    public int Bands { get; }
    public int Rows { get; private set; }
    public double Seconds { get; private set; }
    /// <summary>Physical ring storage. Direct edits require InvalidateHistory before rendering.</summary>
    public float[][] History { get; private set; }
    public float[][] Spectrum { get; }
    /// <summary>Overlap-weighted power mean: 0 = mean, .5 = RMS, 1 = maximum.</summary>
    public double SpectrumAggregation
    {
        get => spectrumAggregation;
        set
        {
            if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
            spectrumAggregation = value;
        }
    }
    /// <summary>Aggregated diagnostic row: residual RMS, observed amplitude, log and complex smoother amplitudes.</summary>
    internal float[][] DiagnosticSpectrum { get; }
    /// <summary>Residual RMS selected or averaged over the same rows as Spectrum.</summary>
    public float[][] ResidualSpectrum { get; }
    public Vector3 Components { get; private set; }
    public float FractalEnergy { get; private set; }
    public double MusicalTime { get; private set; }
    public double MusicalVelocity { get; private set; }
    public float FractalDepth => (float)(10 * Math.Clamp(MusicalVelocity / .5, 0, 1));
    public float Flash { get; private set; }
    public float Energy { get; private set; }
    public double Now => timeProvider.GetElapsedTime(started).TotalSeconds;
    internal double TimeAt(long timestamp) => AudioClock.Seconds(timestamp
        - AudioClock.ConvertTimestamp(started, timeProvider.TimestampFrequency));
    double HostNow => AudioClock.Seconds(AudioClock.Now(timeProvider));
    public float Shift => (float)(((position % Rows + Rows) % Rows + .5) / Rows);
    public float TimeScale => (float)(Seconds * rate / Rows);
    public float AvailableSpan => historyCleared ? 0 : (float)(Math.Max(0, position - Math.Max(firstRow, newestRow - Rows + 1)) / Rows);
    public float NewestAge => (float)(Math.Max(0, position - newestRow) / Rows);
    public long DroppedRows => Interlocked.Read(ref dropped);
    public long LatestCaptureTimestamp { get; private set; }
    public long? LatestSourceTimestamp { get; private set; }
    public SourceWatermark? LatestProgress { get; private set; }

    public AudioHistory(int bands, double analysisRate, double seconds = 1, double presentationDelay = 0,
        TimeProvider? timeProvider = null)
        : this(bands, analysisRate, seconds, presentationDelay, timeProvider ?? TimeProvider.System, null) { }

    AudioHistory(int bands, double analysisRate, double seconds, double presentationDelay,
        TimeProvider timeProvider, long? origin)
    {
        this.timeProvider = timeProvider;
        started = origin ?? timeProvider.GetTimestamp();
        if (bands <= 0 || analysisRate <= 0 || !double.IsFinite(analysisRate)
            || seconds <= 0 || !double.IsFinite(seconds)
            || presentationDelay < 0 || !double.IsFinite(presentationDelay)) throw new ArgumentOutOfRangeException();
        Bands = bands; rate = analysisRate; Seconds = seconds; PresentationDelay = presentationDelay;
        Rows = (int)Math.Clamp(Math.Ceiling((seconds + presentationDelay) * rate) + 2, 4, 16384);
        Spectrum = [new float[4 * bands], new float[4 * bands]];
        // RGBA row for upload into the existing spectrum textures.
        DiagnosticSpectrum = [new float[4 * bands], new float[4 * bands]];
        History = [new float[4 * bands * Rows], new float[4 * bands * Rows]];
        InvalidateHistory();
        sum = new double[8 * bands];
        diagnosticSum = new double[8 * bands]; diagnosticMaximum = new double[8 * bands];
        residualSum = new double[2 * bands];
        maximum = new double[8 * bands];
        residualMaximum = new double[2 * bands];
        ResidualSpectrum = [new float[bands], new float[bands]];
        handoffRows = Math.Max(2, (int)Math.Ceiling((HandoffSeconds + presentationDelay) * rate));
        pending = Mailbox(handoffRows);
        // Offline input has an initially history-sized mailbox. Retention edits preserve its queue and capacity.
        offline = Mailbox(Math.Min(32768, Rows * 2));
    }

    // A new analysis generation rewinds row numbers, but must not rewind the renderer clock.
    internal AudioHistory Restart(AnalysisOutput input) => new(input.Domain.Length, input.AnalysisRate,
        Seconds, PresentationDelay, timeProvider, started) { SpectrumAggregation = SpectrumAggregation };

    internal sealed record HistoryConfiguration(double Seconds, int Rows, float[][] History);

    // Allocate first so a failed GPU allocation can leave the current history intact.
    internal HistoryConfiguration PrepareHistory(double seconds)
    {
        new GraphicsOptions(seconds).Validate();
        int rows = (int)Math.Clamp(Math.Ceiling((seconds + PresentationDelay) * rate) + 2, 4, 16384);
        return new(seconds, rows, [new float[4 * Bands * rows], new float[4 * Bands * rows]]);
    }

    internal void CommitHistory(HistoryConfiguration configuration)
    {
        Seconds = configuration.Seconds;
        Rows = configuration.Rows;
        History = configuration.History;
        firstRow = newestRow + 1;
        historyCleared = true;
        InvalidateHistory();
    }

    Channel<Row> Mailbox(int capacity) => Channel.CreateBounded<Row>(new BoundedChannelOptions(capacity)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false,
    }, _ => DropRows(1));

    void DropRows(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref dropped, count);
        Progress?.MarkGap();
    }

    public void Post(SampleEvent<double> input, int channelCount)
    {
        if (input.SampleCount != Bands * Splitter.OutputStride || channelCount < 1)
            throw new ArgumentException("Analysis dimensions do not match the renderer.");
        bool timed = input.SampleEndTimestamp.HasValue;
        if (timed != input.SampleStartTimestamp.HasValue
            || timed && input.SampleEndTimestamp <= input.SampleStartTimestamp)
            throw new ArgumentException("A timed row must have a positive source interval.");
        if (!input.ResidualRms.IsEmpty && input.ResidualRms.Length != Bands * channelCount)
            throw new ArgumentException("Residual dimensions do not match the renderer.");
        if (!input.ObservedAmplitude.IsEmpty && input.ObservedAmplitude.Length != Bands * channelCount)
            throw new ArgumentException("Observed amplitude dimensions do not match the renderer.");
        if (!input.LogSmoothedAmplitude.IsEmpty && input.LogSmoothedAmplitude.Length != Bands * channelCount)
            throw new ArgumentException("Log smoother dimensions do not match the renderer.");
        if (!input.ComplexSmoothedAmplitude.IsEmpty && input.ComplexSmoothedAmplitude.Length != Bands * channelCount)
            throw new ArgumentException("Complex smoother dimensions do not match the renderer.");
        // Aggregated diagnostic RGBA: residual RMS, raw magnitude, mode A magnitude, mode B magnitude.
        var diagnostics = new float[Bands * 8];
        var residual = new float[Bands * 2];
        var owned = new float[Bands * 8];
        for (var c = 0; c < 2; c++)
        {
            int channel = Math.Min(c, channelCount - 1);
            if (!input.ResidualRms.IsEmpty)
                for (int k = 0; k < Bands; k++)
                {
                    double value = input.ResidualRms.Span[channel * Bands + k];
                    residual[c * Bands + k] = double.IsFinite(value) ? (float)Math.Clamp(value, 0, float.MaxValue) : 0;
                }
            for (int k = 0; k < Bands; k++) diagnostics[(c * Bands + k) * 4] = residual[c * Bands + k];
            if (!input.ObservedAmplitude.IsEmpty)
                for (int k = 0; k < Bands; k++)
                {
                    double value = input.ObservedAmplitude.Span[channel * Bands + k];
                    diagnostics[(c * Bands + k) * 4 + 1] = double.IsFinite(value) ? (float)Math.Clamp(value, 0, float.MaxValue) : 0;
                }
            if (!input.LogSmoothedAmplitude.IsEmpty)
                for (int k = 0; k < Bands; k++)
                {
                    double value = input.LogSmoothedAmplitude.Span[channel * Bands + k];
                    diagnostics[(c * Bands + k) * 4 + 2] = double.IsFinite(value) ? (float)Math.Clamp(value, 0, float.MaxValue) : 0;
                }
            if (!input.ComplexSmoothedAmplitude.IsEmpty)
                for (int k = 0; k < Bands; k++)
                {
                    double value = input.ComplexSmoothedAmplitude.Span[channel * Bands + k];
                    diagnostics[(c * Bands + k) * 4 + 3] = double.IsFinite(value) ? (float)Math.Clamp(value, 0, float.MaxValue) : 0;
                }
            var src = input.GetChannel(channel).Span;
            for (var i = 0; i < src.Length; i++)
                owned[c * Bands * 4 + i] = double.IsFinite(src[i]) ? (float)Math.Clamp(src[i], 0, float.MaxValue) : 0;
        }
        var row = new Row(input.AnalysisRow, input.CaptureTimestamp, input.AnalysisTimestamp,
            timed ? AudioClock.Seconds(input.SampleStartTimestamp!.Value) : input.AnalysisRow / rate,
            timed ? AudioClock.Seconds(input.SampleEndTimestamp!.Value) : (input.AnalysisRow + 1) / rate,
            HostNow, owned, residual, diagnostics, input.DiscontinuityGeneration, timed, input.StreamClock, input.SourceWatermark);
        (timed ? pending : offline).Writer.TryWrite(row);
    }

    public void Advance(double dt, long? presentationTimestamp = null)
    {
        long hostTicks = AudioClock.Now(timeProvider);
        double host = AudioClock.Seconds(hostTicks);
        double target = AudioClock.Seconds((presentationTimestamp ?? hostTicks)
            - (long)Math.Round(PresentationDelay * System.Diagnostics.Stopwatch.Frequency));
        // Bounded work even if a producer is concurrently filling the mailbox.
        for (var i = 0; i < handoffRows && pending.Reader.TryRead(out var row); i++)
        {
            if (scheduled.Count > 0 && scheduled.Last().Generation != row.Generation)
            {
                DropRows(scheduled.Count);
                scheduled.Clear();
            }
            // Upload rows as soon as they arrive, even ahead of a delayed presentation
            // cursor. Visibility and live envelopes are selected separately below.
            if (!Store(row)) continue;
            // One native sample per accepted row, before presentation scheduling.
            if (row.Completed != 0)
            {
                long accepted = AudioClock.Now(timeProvider);
                if (accepted >= row.Completed)
                    Profile?.Record("render.analysis_handoff_ms", AudioClock.Seconds(accepted - row.Completed) * 1000, accepted);
            }
            scheduled.Enqueue(row);
            while (scheduled.Count > handoffRows) { scheduled.Dequeue(); DropRows(1); }
        }
        while (scheduled.TryPeek(out var row) && row.End <= target)
        {
            scheduled.Dequeue();
            Consume(row, host);
        }
        for (var i = 0; i < Math.Min(32768, Rows * 2) && offline.Reader.TryRead(out var row); i++)
            if (Store(row)) Consume(row, host);

        if (newestRow < 0) return;
        double end = live ? Math.Min(target, newestEnd) : newestEnd - PresentationDelay;
        double age = live ? target - newestEnd : host - newestArrival;
        if (haveConsumed && (!haveCursor || end > cursor))
        {
            double start = haveCursor ? cursor : recent.TryPeek(out var first) ? first.Start : end;
            if (live) start = Math.Max(start, end - Retention);
            aggregateStart = start;
            Aggregate(start, end);
            cursor = end;
            haveCursor = true;
        }
        else if (haveConsumed && haveCursor && appliedSpectrumAggregation != SpectrumAggregation)
            Aggregate(aggregateStart, cursor);
        appliedSpectrumAggregation = SpectrumAggregation;
        if (haveConsumed && age > Retention)
        {
            ClearLive();
            var silenceEnd = live ? target : newestEnd + age;
            if (haveState && silenceEnd > stateTime)
            {
                DriveZero(silenceEnd - stateTime);
                stateTime = silenceEnd;
            }
        }
        // Row arrivals update storage, never the live scroll clock. Anchor once so
        // batching, capture jitter and the retention timeout cannot move existing pixels.
        // NewestAge masks the unwritten strip while delayed rows are in flight.
        position = live ? historyClockRow + (target - historyClockTime) * rate
            : newestRow + (end - newestEnd) * rate;
        if (!live && age > Retention) position = newestRow + age * rate;
        if (live && haveConsumed)
        {
            if (presentationTimestamp is { } presentation && LatestSourceTimestamp is { } source)
                Profile?.Record("render.total_audio_age_ms", Math.Max(0, AudioClock.Seconds(presentation - source)) * 1000, hostTicks);
            Profile?.Record("render.presented_audio_age_ms", Math.Max(0, host - end) * 1000);
            Profile?.Record("render.source_age_ms", (host - newestEnd) * 1000);
        }
    }

    bool Store(Row row)
    {
        bool changed = generation != row.Generation;
        bool clockChanged = row.Live && row.Clock.HasValue && streamClock.HasValue && row.Clock != streamClock;
        if (changed)
        {
            generation = row.Generation;
            recent.Clear();
            haveCursor = false;
            haveConsumed = false;
            LatestProgress = null;
            ClearLive();
            Flash = 0;
        }
        if (newestRow >= 0 && (row.Index <= newestRow || clockChanged))
        {
            if (!changed && !clockChanged) { DropRows(1); return false; }
            foreach (var history in History) Array.Clear(history);
            InvalidateHistory();
            newestRow = row.Index - 1;
            firstRow = row.Index;
            haveState = false;
            haveConsumed = false;
            consumedRow = -1;
            haveHistoryClock = false;
        }
        if (row.Live && (!haveHistoryClock || row.Clock.HasValue && row.Clock != streamClock))
        {
            // The driver correlation can precede the first retained/complete analysis row.
            // In row-center coordinates, the end of stream frame zero is row -1.
            historyClockRow = row.Clock is { } clock ? clock.FramePosition * (rate / clock.SampleRate) - 1 : row.Index;
            historyClockTime = row.Clock is { } source ? AudioClock.Seconds(source.Timestamp) : row.End;
            streamClock = row.Clock;
            haveHistoryClock = true;
        }
        if (!row.Live) haveHistoryClock = false;
        if (newestRow < 0 && row.Live) firstRow = row.Index;
        if (historyCleared) { firstRow = row.Index; historyCleared = false; }
        long clearFrom = Math.Max(newestRow + 1, row.Index - Rows + 1);
        MarkHistoryRows((int)((clearFrom % Rows + Rows) % Rows), (int)(row.Index - clearFrom + 1));
        for (long clear = clearFrom; clear <= row.Index; clear++)
        {
            int slot = (int)((clear % Rows + Rows) % Rows);
            foreach (var history in History) Array.Clear(history, slot * Bands * 4, Bands * 4);
        }
        int offset = (int)((row.Index % Rows + Rows) % Rows) * Bands * 4;
        for (var c = 0; c < 2; c++) row.Samples.AsSpan(c * Bands * 4, Bands * 4).CopyTo(History[c].AsSpan(offset));
        newestRow = row.Index;
        live = row.Live;
        return true;
    }

    void Consume(Row row, double host)
    {
        newestEnd = row.End; newestArrival = row.Arrival;
        haveConsumed = true;
        LatestCaptureTimestamp = row.Capture;
        LatestProgress = row.Progress;
        LatestSourceTimestamp = row.Live ? (long)Math.Round(row.End * System.Diagnostics.Stopwatch.Frequency) : null;
        recent.Enqueue(row);
        double keepFrom = row.End - (row.Live ? Retention : Rows / rate);
        while (recent.TryPeek(out var old) && old.End <= keepFrom) recent.Dequeue();
        while (recent.Count > (row.Live ? handoffRows : Rows)) recent.Dequeue();
        if (consumedRow >= 0 && row.Index != consumedRow + 1) { ClearLive(); Flash = 0; }
        consumedRow = row.Index;
        // Untimed replay rows have a logical sample clock, independent of time spent paused in the host.
        if (!row.Live && haveState && stateTime > row.Start) stateTime = row.Start;
        if (haveState && row.Start > stateTime) DriveZero(row.Start - stateTime);
        double duration = row.End - Math.Max(row.Start, haveState ? stateTime : row.Start);
        if (duration > 0)
        {
            if (!row.Live || host - PresentationDelay - row.End <= Retention) Drive(row.Samples, duration);
            else DriveZero(duration);
        }
        stateTime = Math.Max(haveState ? stateTime : row.End, row.End);
        haveState = true;
        if (row.Capture != 0) Profile?.Elapsed("render.capture_to_consume_ms", row.Capture);
        if (row.Completed != 0) Profile?.Elapsed("render.analysis_to_consume_ms", row.Completed);
    }

    void Aggregate(double start, double end)
    {
        Array.Clear(sum);
        Array.Clear(residualSum);
        Array.Clear(diagnosticSum);
        double width = end - start;
        if (width <= 0) return;
        double a = SpectrumAggregation;
        if (a > 0)
        {
            Array.Clear(maximum);
            Array.Clear(residualMaximum);
            Array.Clear(diagnosticMaximum);
            foreach (var row in recent)
            {
                if (row.End <= start || row.Start >= end) continue;
                for (var i = 0; i < diagnosticMaximum.Length; i++) diagnosticMaximum[i] = Math.Max(diagnosticMaximum[i], row.Diagnostics[i]);
                for (var i = 0; i < maximum.Length; i++) maximum[i] = Math.Max(maximum[i], row.Samples[i]);
                for (var i = 0; i < residualMaximum.Length; i++) residualMaximum[i] = Math.Max(residualMaximum[i], row.Residual[i]);
            }
        }
        if (a == 1)
        {
            maximum.CopyTo(sum, 0);
            diagnosticMaximum.CopyTo(diagnosticSum, 0);
            residualMaximum.CopyTo(residualSum, 0);
        }
        else
        {
            double p = 1 / (1 - a);
            foreach (var row in recent)
            {
                double weight = Math.Max(0, Math.Min(end, row.End) - Math.Max(start, row.Start)) / width;
                if (weight == 0) continue;
                Accumulate(row.Diagnostics, diagnosticSum, diagnosticMaximum, weight, p, a > 0);
                Accumulate(row.Samples, sum, maximum, weight, p, a > 0);
                Accumulate(row.Residual, residualSum, residualMaximum, weight, p, a > 0);
            }
            if (a > 0)
            {
                FinishPowerMean(diagnosticSum, diagnosticMaximum, 1 - a);
                FinishPowerMean(sum, maximum, 1 - a);
                FinishPowerMean(residualSum, residualMaximum, 1 - a);
            }
        }
        for (var c = 0; c < 2; c++)
            for (var i = 0; i < Bands * 4; i++) Spectrum[c][i] = (float)Math.Clamp(sum[c * Bands * 4 + i], 0, float.MaxValue);
        for (var c = 0; c < 2; c++)
            for (var k = 0; k < Bands; k++) ResidualSpectrum[c][k] = (float)Math.Clamp(residualSum[c * Bands + k], 0, float.MaxValue);
        for (var c = 0; c < 2; c++)
            for (var i = 0; i < Bands * 4; i++) DiagnosticSpectrum[c][i] = (float)Math.Clamp(diagnosticSum[c * Bands * 4 + i], 0, float.MaxValue);
        double power = 0;
        for (var i = 3; i < sum.Length; i += 4) power += sum[i] * sum[i];
        Energy = (float)Math.Sqrt(power / (2 * Bands));
    }

    static void Accumulate(float[] values, double[] totals, double[] peaks, double weight, double p, bool normalize)
    {
        for (var i = 0; i < totals.Length; i++)
        {
            if (!normalize) totals[i] += weight * values[i];
            else if (peaks[i] > 0)
            {
                double x = values[i] / peaks[i];
                totals[i] += weight * (p == 2 ? x * x : Math.Pow(x, p));
            }
        }
    }

    static void FinishPowerMean(double[] totals, double[] peaks, double inversePower)
    {
        // Normalize before exponentiation to avoid overflow even arbitrarily close to Max.
        // Missing intervals contribute zero; do not renormalize the overlap weights.
        for (var i = 0; i < totals.Length; i++)
            totals[i] = totals[i] > 0 ? peaks[i] * Math.Exp(inversePower * Math.Log(totals[i])) : 0;
    }

    void ClearLive()
    {
        foreach (var spectrum in Spectrum) Array.Clear(spectrum);
        foreach (var residual in ResidualSpectrum) Array.Clear(residual);
        foreach (var diagnostic in DiagnosticSpectrum) Array.Clear(diagnostic);
        Energy = 0;
    }

    void Drive(float[] samples, double duration)
    {
        double power = 0, transientPower = 0, tonePower = 0, noisePower = 0, transientMean = 0;
        for (var i = 0; i < samples.Length; i += 4)
        {
            power += (double)samples[i + 3] * samples[i + 3];
            transientPower += (double)samples[i + 1] * samples[i + 1];
            tonePower += (double)samples[i] * samples[i];
            noisePower += (double)samples[i + 2] * samples[i + 2];
            transientMean += samples[i + 1];
        }
        static float Bounded(double power, int count)
        {
            double rms = Math.Sqrt(power / count);
            return (float)(rms / (.2 + rms));
        }
        var demand = new Vector3(Bounded(tonePower, 2 * Bands), Bounded(transientPower, 2 * Bands), Bounded(noisePower, 2 * Bands));
        Components += (demand - Components) * new Vector3((float)(1 - Math.Exp(-duration / .06)),
            (float)(1 - Math.Exp(-duration / .12)), (float)(1 - Math.Exp(-duration / .15)));
        double energy = Math.Sqrt(power / (2 * Bands));
        double energyDemand = energy / (.06 + energy);
        FractalEnergy += (float)((energyDemand - FractalEnergy) * (1 - Math.Exp(-duration / .12)));
        double flash = transientMean / (2 * Bands);
        Flash += (float)((flash - Flash) * (1 - Math.Exp(-duration / (flash > Flash ? .005 : .045))));
        Move(3 * energy + 6 * power / (2 * Bands) + 4 * Math.Sqrt(transientPower / (2 * Bands)), duration);
    }

    void DriveZero(double duration)
    {
        Components *= (float)Math.Exp(-duration / .35);
        FractalEnergy *= (float)Math.Exp(-duration / .35);
        Flash *= (float)Math.Exp(-duration / .045);
        Move(0, duration);
    }

    void Move(double target, double duration)
    {
        double inertia = target > MusicalVelocity ? .16 : 1.6;
        double response = 1 - Math.Exp(-duration / inertia);
        MusicalTime += target * duration + (MusicalVelocity - target) * inertia * response;
        MusicalVelocity += (target - MusicalVelocity) * response;
    }
}

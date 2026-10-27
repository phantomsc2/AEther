using System.Threading.Channels;
using System.Numerics;

namespace AEther.Vortice;

/// <summary>Owned handoff. History, aggregates and envelopes are touched only by the render thread.</summary>
public sealed class AudioHistory
{
    const double Retention = .05, HandoffSeconds = .1;
    sealed record Row(long Index, long Capture, long Completed, double Start, double End,
        double Arrival, float[] Samples, float[] Residual, float[] Momentary, long Generation, bool Live, AudioStreamClock? Clock);
    readonly Channel<Row> pending, offline;
    readonly Queue<Row> scheduled = new(), recent = new();
    readonly TimeProvider timeProvider;
    readonly long started;
    readonly double rate;
    readonly int handoffRows;
    readonly double[] sum, residualSum;
    long newestRow = -1, consumedRow = -1, firstRow, generation = -1;
    double position, newestEnd, newestArrival, cursor, stateTime;
    double historyClockTime;
    double historyClockRow;
    AudioStreamClock? streamClock;
    bool haveHistoryClock;
    bool haveCursor, haveState, haveConsumed, live;
    long dropped;
    int dirtyStart, dirtyCount;

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
    public double PresentationDelay { get; }
    public int Bands { get; }
    public int Rows { get; }
    public double Seconds { get; }
    /// <summary>Physical ring storage. Direct edits require InvalidateHistory before rendering.</summary>
    public float[][] History { get; }
    public float[][] Spectrum { get; }
    /// <summary>Newest analysis block consumed this display frame; zero when no new block became due.</summary>
    internal float[][] BlockSpectrum { get; }
    internal float[][] BlockResidualSpectrum { get; }
    internal float[][] BlockMomentaryResidual { get; }
    /// <summary>Residual RMS averaged over the same rows as Spectrum.</summary>
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
    public float AvailableSpan => (float)(Math.Max(0, position - Math.Max(firstRow, newestRow - Rows + 1)) / Rows);
    public float NewestAge => (float)(Math.Max(0, position - newestRow) / Rows);
    public long DroppedRows => Interlocked.Read(ref dropped);
    public long LatestCaptureTimestamp { get; private set; }
    public long? LatestSourceTimestamp { get; private set; }

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
        BlockSpectrum = [new float[4 * bands], new float[4 * bands]];
        // RGBA row for upload into the existing spectrum textures.
        BlockMomentaryResidual = [new float[4 * bands], new float[4 * bands]];
        BlockResidualSpectrum = [new float[bands], new float[bands]];
        History = [new float[4 * bands * Rows], new float[4 * bands * Rows]];
        InvalidateHistory();
        sum = new double[8 * bands];
        residualSum = new double[2 * bands];
        ResidualSpectrum = [new float[bands], new float[bands]];
        handoffRows = Math.Max(2, (int)Math.Ceiling((HandoffSeconds + presentationDelay) * rate));
        pending = Mailbox(handoffRows);
        // Legacy/offline fixtures can seed an entire history without pretending it is live capture.
        offline = Mailbox(Math.Min(32768, Rows * 2));
    }

    // A new analysis generation rewinds row numbers, but must not rewind the renderer clock.
    internal AudioHistory Restart(GraphicsInput input) => new(input.Domain.Length, input.AnalysisRate,
        input.HistorySeconds, PresentationDelay, timeProvider, started);

    Channel<Row> Mailbox(int capacity) => Channel.CreateBounded<Row>(new BoundedChannelOptions(capacity)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
        AllowSynchronousContinuations = false,
    }, _ => Interlocked.Increment(ref dropped));

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
        if (!input.MomentaryResidual.IsEmpty && input.MomentaryResidual.Length != Bands * channelCount)
            throw new ArgumentException("Momentary residual dimensions do not match the renderer.");
        if (!input.ObservedAmplitude.IsEmpty && input.ObservedAmplitude.Length != Bands * channelCount)
            throw new ArgumentException("Observed amplitude dimensions do not match the renderer.");
        if (!input.LogSmoothedAmplitude.IsEmpty && input.LogSmoothedAmplitude.Length != Bands * channelCount)
            throw new ArgumentException("Log smoother dimensions do not match the renderer.");
        if (!input.ComplexSmoothedAmplitude.IsEmpty && input.ComplexSmoothedAmplitude.Length != Bands * channelCount)
            throw new ArgumentException("Complex smoother dimensions do not match the renderer.");
        // Diagnostic RGBA: signed residual, raw magnitude, mode A magnitude, mode B magnitude.
        var momentary = new float[Bands * 8];
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
            if (!input.MomentaryResidual.IsEmpty)
                for (int k = 0; k < Bands; k++)
                {
                    double value = input.MomentaryResidual.Span[channel * Bands + k];
                    momentary[(c * Bands + k) * 4] = double.IsFinite(value) ? (float)Math.Clamp(value, -float.MaxValue, float.MaxValue) : 0;
                }
            if (!input.ObservedAmplitude.IsEmpty)
                for (int k = 0; k < Bands; k++)
                {
                    double value = input.ObservedAmplitude.Span[channel * Bands + k];
                    momentary[(c * Bands + k) * 4 + 1] = double.IsFinite(value) ? (float)Math.Clamp(value, 0, float.MaxValue) : 0;
                }
            if (!input.LogSmoothedAmplitude.IsEmpty)
                for (int k = 0; k < Bands; k++)
                {
                    double value = input.LogSmoothedAmplitude.Span[channel * Bands + k];
                    momentary[(c * Bands + k) * 4 + 2] = double.IsFinite(value) ? (float)Math.Clamp(value, 0, float.MaxValue) : 0;
                }
            if (!input.ComplexSmoothedAmplitude.IsEmpty)
                for (int k = 0; k < Bands; k++)
                {
                    double value = input.ComplexSmoothedAmplitude.Span[channel * Bands + k];
                    momentary[(c * Bands + k) * 4 + 3] = double.IsFinite(value) ? (float)Math.Clamp(value, 0, float.MaxValue) : 0;
                }
            var src = input.GetChannel(channel).Span;
            for (var i = 0; i < src.Length; i++)
                owned[c * Bands * 4 + i] = double.IsFinite(src[i]) ? (float)Math.Clamp(src[i], 0, float.MaxValue) : 0;
        }
        var row = new Row(input.AnalysisRow, input.CaptureTimestamp, input.AnalysisTimestamp,
            timed ? AudioClock.Seconds(input.SampleStartTimestamp!.Value) : input.AnalysisRow / rate,
            timed ? AudioClock.Seconds(input.SampleEndTimestamp!.Value) : (input.AnalysisRow + 1) / rate,
            HostNow, owned, residual, momentary, input.DiscontinuityGeneration, timed, input.StreamClock);
        (timed ? pending : offline).Writer.TryWrite(row);
    }

    public void Advance(double dt, long? presentationTimestamp = null)
    {
        foreach (var channel in BlockSpectrum) Array.Clear(channel);
        foreach (var channel in BlockMomentaryResidual) Array.Clear(channel);
        foreach (var channel in BlockResidualSpectrum) Array.Clear(channel);
        long hostTicks = AudioClock.Now(timeProvider);
        double host = AudioClock.Seconds(hostTicks);
        double target = AudioClock.Seconds((presentationTimestamp ?? hostTicks)
            - (long)Math.Round(PresentationDelay * System.Diagnostics.Stopwatch.Frequency));
        // Bounded work even if a producer is concurrently filling the mailbox.
        for (var i = 0; i < handoffRows && pending.Reader.TryRead(out var row); i++)
        {
            if (scheduled.Count > 0 && scheduled.Last().Generation != row.Generation)
            {
                Interlocked.Add(ref dropped, scheduled.Count);
                scheduled.Clear();
            }
            // Upload rows as soon as they arrive, even ahead of a delayed presentation
            // cursor. Visibility and live envelopes are selected separately below.
            if (!Store(row)) continue;
            scheduled.Enqueue(row);
            while (scheduled.Count > handoffRows) { scheduled.Dequeue(); Interlocked.Increment(ref dropped); }
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
            Aggregate(start, end);
            cursor = end;
            haveCursor = true;
        }
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
            ClearLive();
            Flash = 0;
        }
        if (newestRow >= 0 && (row.Index <= newestRow || clockChanged))
        {
            if (!changed && !clockChanged) { Interlocked.Increment(ref dropped); return false; }
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
        // The diagnostic spectrum exposes delivery cadence. Never hold, average,
        // or replay older blocks across subsequent display frames.
        if (!row.Live || host - PresentationDelay - row.End <= Retention)
            for (int channel = 0; channel < 2; channel++)
            {
                row.Samples.AsSpan(channel * Bands * 4, Bands * 4).CopyTo(BlockSpectrum[channel]);
                row.Residual.AsSpan(channel * Bands, Bands).CopyTo(BlockResidualSpectrum[channel]);
                row.Momentary.AsSpan(channel * Bands * 4, Bands * 4).CopyTo(BlockMomentaryResidual[channel]);
            }
        LatestCaptureTimestamp = row.Capture;
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
        double width = end - start;
        if (width <= 0) return;
        foreach (var row in recent)
        {
            double weight = Math.Max(0, Math.Min(end, row.End) - Math.Max(start, row.Start)) / width;
            if (weight == 0) continue;
            for (var i = 0; i < sum.Length; i++) sum[i] += weight * row.Samples[i];
            for (var i = 0; i < residualSum.Length; i++) residualSum[i] += weight * row.Residual[i];
        }
        for (var c = 0; c < 2; c++)
            for (var i = 0; i < Bands * 4; i++) Spectrum[c][i] = (float)Math.Clamp(sum[c * Bands * 4 + i], 0, float.MaxValue);
        for (var c = 0; c < 2; c++)
            for (var k = 0; k < Bands; k++) ResidualSpectrum[c][k] = (float)Math.Clamp(residualSum[c * Bands + k], 0, float.MaxValue);
        double power = 0;
        for (var i = 3; i < sum.Length; i += 4) power += sum[i] * sum[i];
        Energy = (float)Math.Sqrt(power / (2 * Bands));
    }

    void ClearLive()
    {
        foreach (var spectrum in Spectrum) Array.Clear(spectrum);
        foreach (var residual in ResidualSpectrum) Array.Clear(residual);
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

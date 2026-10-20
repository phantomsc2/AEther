using System.Threading.Channels;
using System.Numerics;

namespace AEther.Vortice;

/// <summary>Owned handoff. History, aggregates and envelopes are touched only by the render thread.</summary>
public sealed class AudioHistory
{
    const double Retention = .05, HandoffSeconds = .1;
    sealed record Row(long Index, long Capture, long Completed, double Start, double End,
        double Arrival, float[] Samples, GlobalEvidence Global, long Generation, bool Live);
    readonly Channel<Row> pending, offline;
    readonly Queue<Row> scheduled = new(), recent = new();
    readonly TimeProvider timeProvider;
    readonly long started;
    readonly double rate;
    readonly int handoffRows;
    readonly double[] sum;
    long newestRow = -1, firstRow, generation = -1;
    double position, newestEnd, newestArrival, cursor, stateTime;
    bool haveCursor, haveState, live;
    long dropped;

    public PipelineProfile? Profile { get; set; }
    public double PresentationDelay { get; }
    public int Bands { get; }
    public int Rows { get; }
    public double Seconds { get; }
    public float[][] History { get; }
    public float[][] Spectrum { get; }
    /// <summary>Duration-weighted frame fields. Support metadata comes from the latest contributing row.</summary>
    public GlobalEvidence GlobalEvidence { get; private set; }
    public Vector4 GlobalSTBR => PackGlobal(GlobalEvidence);
    public float[] GlobalHistory { get; }
    public Vector3 Components { get; private set; }
    public float FractalEnergy { get; private set; }
    public double MusicalTime { get; private set; }
    public double MusicalVelocity { get; private set; }
    public float FractalDepth => (float)(10 * Math.Clamp(MusicalVelocity / .5, 0, 1));
    public float Flash { get; private set; }
    public float Energy { get; private set; }
    public double Now => timeProvider.GetElapsedTime(started).TotalSeconds;
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
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        started = this.timeProvider.GetTimestamp();
        if (bands <= 0 || analysisRate <= 0 || !double.IsFinite(analysisRate)
            || seconds <= 0 || !double.IsFinite(seconds)
            || presentationDelay < 0 || !double.IsFinite(presentationDelay)) throw new ArgumentOutOfRangeException();
        Bands = bands; rate = analysisRate; Seconds = seconds; PresentationDelay = presentationDelay;
        Rows = (int)Math.Clamp(Math.Ceiling((seconds + presentationDelay) * rate) + 2, 4, 16384);
        Spectrum = [new float[4 * bands], new float[4 * bands]];
        History = [new float[4 * bands * Rows], new float[4 * bands * Rows]];
        GlobalHistory = new float[4 * Rows];
        sum = new double[8 * bands];
        handoffRows = Math.Max(2, (int)Math.Ceiling((HandoffSeconds + presentationDelay) * rate));
        pending = Mailbox(handoffRows);
        // Legacy/offline fixtures can seed an entire history without pretending it is live capture.
        offline = Mailbox(Math.Min(32768, Rows * 2));
    }

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
        var owned = new float[Bands * 8];
        for (var c = 0; c < 2; c++)
        {
            var src = input.GetChannel(Math.Min(c, channelCount - 1)).Span;
            for (var i = 0; i < src.Length; i++)
                owned[c * Bands * 4 + i] = double.IsFinite(src[i]) ? (float)Math.Clamp(src[i], 0, float.MaxValue) : 0;
        }
        var row = new Row(input.AnalysisRow, input.CaptureTimestamp, input.AnalysisTimestamp,
            timed ? AudioClock.Seconds(input.SampleStartTimestamp!.Value) : input.AnalysisRow / rate,
            timed ? AudioClock.Seconds(input.SampleEndTimestamp!.Value) : (input.AnalysisRow + 1) / rate,
            HostNow, owned, input.GlobalEvidence, input.DiscontinuityGeneration, timed);
        (timed ? pending : offline).Writer.TryWrite(row);
    }

    static Vector4 PackGlobal(GlobalEvidence value)
    {
        static float Finite(double x) => double.IsFinite(x) ? (float)Math.Clamp(x, -float.MaxValue, float.MaxValue) : 0;
        return new(Math.Max(0, Finite(value.Structure)), Finite(value.Transient),
            Math.Max(0, Finite(value.Background)), Math.Max(0, Finite(value.InteractionRms)));
    }

    public void Advance(double dt)
    {
        long hostTicks = AudioClock.Now(timeProvider);
        double host = AudioClock.Seconds(hostTicks);
        double target = AudioClock.Seconds(hostTicks - (long)Math.Round(PresentationDelay * System.Diagnostics.Stopwatch.Frequency));
        // Bounded work even if a producer is concurrently filling the mailbox.
        for (var i = 0; i < handoffRows && pending.Reader.TryRead(out var row); i++)
        {
            if (generation >= 0 && row.Generation != generation)
            {
                ClearLive(); recent.Clear(); haveCursor = false; Flash = 0;
            }
            if (scheduled.Count > 0 && scheduled.Last().Generation != row.Generation)
            {
                Interlocked.Add(ref dropped, scheduled.Count);
                scheduled.Clear();
            }
            scheduled.Enqueue(row);
            while (scheduled.Count > handoffRows) { scheduled.Dequeue(); Interlocked.Increment(ref dropped); }
        }
        while (scheduled.TryPeek(out var row) && row.End <= target)
        {
            scheduled.Dequeue();
            Consume(row, host);
        }
        for (var i = 0; i < Math.Min(32768, Rows * 2) && offline.Reader.TryRead(out var row); i++)
            Consume(row, host);

        if (newestRow < 0) return;
        double end = live ? Math.Min(target, newestEnd) : newestEnd - PresentationDelay;
        double age = live ? target - newestEnd : host - newestArrival;
        if (!haveCursor || end > cursor)
        {
            double start = haveCursor ? cursor : recent.TryPeek(out var first) ? first.Start : end;
            if (live) start = Math.Max(start, end - Retention);
            Aggregate(start, end);
            cursor = end;
            haveCursor = true;
        }
        if (age > Retention)
        {
            ClearLive();
            var silenceEnd = live ? target : newestEnd + age;
            if (haveState && silenceEnd > stateTime)
            {
                DriveZero(silenceEnd - stateTime);
                stateTime = silenceEnd;
            }
        }
        position = newestRow + (end - newestEnd) * rate;
        if (age > Retention) position = newestRow + age * rate;
        if (live)
        {
            Profile?.Record("render.presented_audio_age_ms", Math.Max(0, host - end) * 1000);
            Profile?.Record("render.source_age_ms", (host - newestEnd) * 1000);
        }
    }

    void Consume(Row row, double host)
    {
        bool changed = generation != row.Generation;
        if (changed)
        {
            generation = row.Generation;
            recent.Clear();
            haveCursor = false;
            ClearLive();
            Flash = 0;
        }
        if (newestRow >= 0 && (row.Index <= newestRow || row.End <= newestEnd))
        {
            if (!changed) { Interlocked.Increment(ref dropped); return; }
            foreach (var history in History) Array.Clear(history);
            Array.Clear(GlobalHistory);
            newestRow = row.Index - 1;
            firstRow = row.Index;
            haveState = false;
        }
        long previous = newestRow;
        for (long clear = Math.Max(newestRow + 1, row.Index - Rows + 1); clear <= row.Index; clear++)
        {
            int slot = (int)((clear % Rows + Rows) % Rows);
            Array.Clear(GlobalHistory, slot * 4, 4);
            foreach (var history in History) Array.Clear(history, slot * Bands * 4, Bands * 4);
        }
        int offset = (int)((row.Index % Rows + Rows) % Rows) * Bands * 4;
        for (var c = 0; c < 2; c++) row.Samples.AsSpan(c * Bands * 4, Bands * 4).CopyTo(History[c].AsSpan(offset));
        PackGlobal(row.Global).CopyTo(GlobalHistory.AsSpan(offset / Bands, 4));
        newestRow = row.Index; newestEnd = row.End; newestArrival = row.Arrival; live = row.Live;
        LatestCaptureTimestamp = row.Capture;
        LatestSourceTimestamp = row.Live ? (long)Math.Round(row.End * System.Diagnostics.Stopwatch.Frequency) : null;
        recent.Enqueue(row);
        double keepFrom = row.End - (row.Live ? Retention : Rows / rate);
        while (recent.TryPeek(out var old) && old.End <= keepFrom) recent.Dequeue();
        while (recent.Count > (row.Live ? handoffRows : Rows)) recent.Dequeue();
        if (previous >= 0 && row.Index != previous + 1) { ClearLive(); Flash = 0; }
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
        double width = end - start;
        if (width <= 0) return;
        double background = 0, transient = 0, structure = 0, residual = 0;
        GlobalEvidence support = default;
        foreach (var row in recent)
        {
            double weight = Math.Max(0, Math.Min(end, row.End) - Math.Max(start, row.Start)) / width;
            if (weight == 0) continue;
            for (var i = 0; i < sum.Length; i++) sum[i] += weight * row.Samples[i];
            static double Finite(double v) => double.IsFinite(v) ? v : 0;
            background += weight * Finite(row.Global.Background);
            transient += weight * Finite(row.Global.Transient);
            structure += weight * Finite(row.Global.Structure);
            residual += weight * Finite(row.Global.InteractionRms);
            support = row.Global;
        }
        for (var c = 0; c < 2; c++)
            for (var i = 0; i < Bands * 4; i++) Spectrum[c][i] = (float)Math.Clamp(sum[c * Bands * 4 + i], 0, float.MaxValue);
        GlobalEvidence = support with { Background = background, Transient = transient, Structure = structure, InteractionRms = residual };
        double power = 0;
        for (var i = 3; i < sum.Length; i += 4) power += sum[i] * sum[i];
        Energy = (float)Math.Sqrt(power / (2 * Bands));
    }

    void ClearLive()
    {
        foreach (var spectrum in Spectrum) Array.Clear(spectrum);
        GlobalEvidence = default;
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

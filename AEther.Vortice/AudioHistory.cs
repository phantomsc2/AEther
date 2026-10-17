using System.Threading.Channels;
using System.Numerics;

namespace AEther.Vortice;

/// <summary>Owned, bounded handoff. Only the render thread touches history or graphics resources.</summary>
public sealed class AudioHistory
{
    readonly Channel<(long Row, long Capture, long Completed, double Arrival, float[] Samples, GlobalEvidence Global)> pending;

    readonly TimeProvider timeProvider;
    readonly long started;
    readonly double rate;
    readonly float[] peak;
    long latestRow = -1;
    double lastAudio = double.NegativeInfinity;
    double anchorTime, position;
    double movementImpulse;
    long anchorRow, newestRow = -1;
    long firstRow;
    bool anchored;
    public PipelineProfile? Profile { get; set; }
    public double PresentationDelay { get; }
    public int Bands { get; }
    public int Rows { get; }
    public double Seconds { get; }
    public float[][] History { get; }
    public float[][] Spectrum { get; }
    /// <summary>Newest complete packet; signed T is retained. Expires with live audio.</summary>
    public GlobalEvidence GlobalEvidence { get; private set; }
    public Vector4 GlobalSTBR => PackGlobal(GlobalEvidence);
    /// <summary>Width-one RGBA history in (structure, signed transient, background, interaction RMS) order.</summary>
    public float[] GlobalHistory { get; }
    /// <summary>Bounded tone/transient/noise envelopes, advanced in audio-row time.</summary>
    public Vector3 Components { get; private set; }
    public float FractalEnergy { get; private set; }
    public double MusicalTime { get; private set; }
    public double MusicalVelocity { get; private set; }
    // Depth travel follows the same smoothed drive as motion. The renderer
    // maps this 0..10 demand to depths 3..configured maximum, independently of AA.
    public float FractalDepth => (float)(10 * Math.Clamp(MusicalVelocity / 0.5, 0, 1));
    public float Flash { get; private set; }
    public float Energy { get; private set; }
    public double Now => timeProvider.GetElapsedTime(started).TotalSeconds;
    public float Shift => (float)(((position % Rows + Rows) % Rows + .5) / Rows);
    public float TimeScale => (float)(Seconds * rate / Rows);
    // Limit sampling to retained row centers, including during startup or a backlog.
    public float AvailableSpan => (float)(Math.Max(0, position - Math.Max(firstRow, newestRow - Rows + 1)) / Rows);
    public float NewestAge => (float)(Math.Max(0, position - newestRow) / Rows);
    public long DroppedRows => Interlocked.Read(ref dropped);
    public long LatestCaptureTimestamp { get; private set; }
    long dropped;

    public AudioHistory(int bands, double analysisRate, double seconds = 1, double presentationDelay = .02,
        TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        started = this.timeProvider.GetTimestamp();
        if (bands <= 0 || analysisRate <= 0 || !double.IsFinite(analysisRate)
            || seconds <= 0 || !double.IsFinite(seconds)
            || presentationDelay < 0 || !double.IsFinite(presentationDelay)) throw new ArgumentOutOfRangeException();
        Bands = bands; rate = analysisRate; Seconds = seconds;
        PresentationDelay = presentationDelay;
        // History duration and presentation delay both consume ring capacity.
        Rows = (int)Math.Clamp(Math.Ceiling((seconds + presentationDelay) * rate) + 2, 4, 16384);
        Spectrum = [new float[4 * bands], new float[4 * bands]];
        History = [new float[4 * bands * Rows], new float[4 * bands * Rows]];
        GlobalHistory = new float[4 * Rows];
        peak = new float[8 * bands];
        pending = Channel.CreateBounded<(long, long, long, double, float[], GlobalEvidence)>(new BoundedChannelOptions(Math.Min(4096, Rows * 2))
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        }, _ => Interlocked.Increment(ref dropped));
    }

    public void Post(SampleEvent<double> input, int channelCount)
    {
        if (input.SampleCount != Bands * Splitter.OutputStride || channelCount < 1)
            throw new ArgumentException("Analysis dimensions do not match the renderer.");
        var owned = new float[Bands * 8];
        for (var c = 0; c < 2; c++)
        {
            var src = input.GetChannel(Math.Min(c, channelCount - 1)).Span;
            for (var i = 0; i < src.Length; i++)
                owned[c * Bands * 4 + i] = double.IsFinite(src[i])
                    ? (float)Math.Clamp(src[i], 0, float.MaxValue) : 0;
        }
        pending.Writer.TryWrite((input.AnalysisRow, input.CaptureTimestamp, input.AnalysisTimestamp, Now, owned, input.GlobalEvidence));
    }

    static Vector4 PackGlobal(GlobalEvidence value)
    {
        static float Finite(double x) => double.IsFinite(x) ? (float)Math.Clamp(x, -float.MaxValue, float.MaxValue) : 0;
        return new(Math.Max(0, Finite(value.Structure)), Finite(value.Transient),
            Math.Max(0, Finite(value.Background)), Math.Max(0, Finite(value.InteractionRms)));
    }

    public void Advance(double dt)
    {
        Flash *= (float)Math.Exp(-dt / .045);
        Array.Clear(peak);

        var now = Now;
        while (pending.Reader.TryRead(out var packet))
        {
            // Loopback can stop producing samples while a player is paused.
            // Wall time continues, but sample-based row numbers do not. Detect
            // that gap using packet arrival (not render timing or frame dt).
            if (anchored && anchorRow + (packet.Arrival - anchorTime - PresentationDelay) * rate
                > packet.Row + .15 * rate)
            {
                foreach (var history in History) Array.Clear(history);
                Array.Clear(GlobalHistory);
                GlobalEvidence = default;
                firstRow = packet.Row;
                latestRow = newestRow = packet.Row - 1;
                anchored = false;
                Flash = 0;
            }
            if (!anchored)
            {
                anchorTime = packet.Arrival;
                anchorRow = packet.Row;
                anchored = true;
            }
            else if (packet.Row > newestRow)
            {
                // Analysis can acquire a lasting delay after capture drops or a
                // queue backlog. Do not keep presenting ahead of every new row:
                // that unwritten interval becomes a permanent hole at the core
                // of scenes that project history outward. Restore the delay
                // reserve at arrival; between packets wall time still advances
                // normally so stopped capture eventually goes completely dark.
                var arrivalRow = anchorRow + (packet.Arrival - anchorTime) * rate;
                if (arrivalRow > packet.Row)
                    anchorTime += (arrivalRow - packet.Row) / rate;
            }
            var row = packet.Row;
            LatestCaptureTimestamp = packet.Capture;
            newestRow = Math.Max(newestRow, row);
            // Skipped analysis rows remain empty, including after ring wrap.
            for (var clear = Math.Max(latestRow + 1, row - Rows + 1); clear <= row; clear++)
            {
                Array.Clear(GlobalHistory, (int)((clear % Rows + Rows) % Rows) * 4, 4);
                for (var c = 0; c < 2; c++)
                {
                    Array.Clear(History[c], (int)((clear % Rows + Rows) % Rows) * Bands * 4, Bands * 4);
                }
            }
            latestRow = Math.Max(latestRow, row);
            if (row < latestRow - Rows + 1) continue;
            if (packet.Capture != 0) Profile?.Elapsed("render.capture_to_consume_ms", packet.Capture);
            if (packet.Completed != 0) Profile?.Elapsed("render.analysis_to_consume_ms", packet.Completed);
            var offset = (int)((row % Rows + Rows) % Rows) * Bands * 4;
            var global = PackGlobal(packet.Global);
            global.CopyTo(GlobalHistory.AsSpan(offset / Bands, 4));
            if (row == latestRow) GlobalEvidence = packet.Global;
            for (var c = 0; c < 2; c++)
            {
                packet.Samples.AsSpan(c * Bands * 4, Bands * 4).CopyTo(History[c].AsSpan(offset));
            }
            for (var i = 0; i < peak.Length; i++) peak[i] = Math.Max(peak[i], packet.Samples[i]);
            var power = 0.0;
            var transientPower = 0.0;
            double tonePower = 0, noisePower = 0;
            for (var i = 0; i < Bands * 8; i += 4)
            {
                // Alpha is raw linear magnitude; RGB are independent robust evidence fields.
                var magnitude = (double)packet.Samples[i + 3];
                power += magnitude * magnitude;
                var transient = (double)packet.Samples[i + 1];
                transientPower += transient * transient;
                tonePower += (double)packet.Samples[i] * packet.Samples[i];
                noisePower += (double)packet.Samples[i + 2] * packet.Samples[i + 2];
                Flash = Math.Max(Flash, packet.Samples[i + 1]);
            }
            Energy = (float)Math.Sqrt(power / (2 * Bands));
            static float Bounded(double power, int count)
            {
                double rms = Math.Sqrt(power / count);
                return (float)(rms / (.2 + rms));
            }
            var demand = new Vector3(Bounded(tonePower, 2 * Bands), Bounded(transientPower, 2 * Bands), Bounded(noisePower, 2 * Bands));
            Components += (demand - Components) * new Vector3((float)(1 - Math.Exp(-1 / (.06 * rate))),
                (float)(1 - Math.Exp(-1 / (.12 * rate))), (float)(1 - Math.Exp(-1 / (.15 * rate))));
            var energyDemand = Energy / (.06f + Energy);
            FractalEnergy += (energyDemand - FractalEnergy) * (float)(1 - Math.Exp(-1 / (.12 * rate)));
            // Accumulate drive by sample duration so frame rate does not change
            // how much force a passage supplies to the slice's momentum.
            movementImpulse += (3 * Energy + 6 * power / (2 * Bands)
                + 4 * Math.Sqrt(transientPower / (2 * Bands))) / rate;
            lastAudio = now;

        }
        if (dt > 0)
        {
            // EMA velocity: attack ten times faster than decay, with a long
            // coast when the music eases off.
            // Integrate both velocity and position analytically for this frame.
            var target = movementImpulse / dt;
            var inertia = target > MusicalVelocity ? .16 : 1.6;
            var response = 1 - Math.Exp(-dt / inertia);
            MusicalTime += target * dt + (MusicalVelocity - target) * inertia * response;
            MusicalVelocity += (target - MusicalVelocity) * response;
            movementImpulse = 0;
        }
        // Maximum over exactly the events drained since the previous frame.
        for (var c = 0; c < 2; c++)
        {
            peak.AsSpan(c * Bands * 4, Bands * 4).CopyTo(Spectrum[c]);
        }
        if (anchored)
        {
            var desired = anchorRow + (now - anchorTime - PresentationDelay) * rate;
            // If a burst exceeds the delay reserve, move forward enough to retain the visible window.
            var minimum = Math.Min(newestRow, Math.Max(0, newestRow - Rows + 1 + Seconds * rate));
            if (desired < minimum)
            {
                // Move the clock anchor with a backlog jump, so presentation
                // continues immediately instead of waiting for wall time to catch up.
                anchorTime -= (minimum - desired) / rate;
                desired = minimum;
            }
            // Presentation keeps advancing even when capture emits no packets.
            // NewestAge masks the unwritten interval instead of wrapping old rows.
            position = Math.Max(desired, minimum);
            if (desired > newestRow) Profile?.Record("render.presentation_stall_ms", (desired - newestRow) * 1000 / rate);
        }
        if (now - lastAudio > .15)
        {
            foreach (var spectrum in Spectrum) Array.Clear(spectrum);
            Energy = 0;
            GlobalEvidence = default;
            Components *= (float)Math.Exp(-dt / .35);
            FractalEnergy *= (float)Math.Exp(-dt / .35);
        }
    }
}

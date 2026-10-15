using System.Diagnostics;
using System.Threading.Channels;

namespace AEther.Vortice;

/// <summary>Owned, bounded handoff. Only the render thread touches history or graphics resources.</summary>
public sealed class AudioHistory
{
    readonly Channel<(long Row, long Capture, long Completed, float[] Samples)> pending;

    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly double rate;
    readonly float[] peak;
    long latestRow = -1;
    double lastAudio = double.NegativeInfinity;
    double anchorTime, position;
    long anchorRow, newestRow = -1;
    bool anchored;
    public PipelineProfile? Profile { get; set; }
    public double PresentationDelay { get; }
    public int Bands { get; }
    public int Rows { get; }
    public double Seconds { get; }
    public float[][] History { get; }
    public float[][] Spectrum { get; }
    public double MusicalTime { get; private set; }
    public float Flash { get; private set; }
    public float Energy { get; private set; }
    public double Now => clock.Elapsed.TotalSeconds;
    public float Shift => (float)(((position % Rows + Rows) % Rows + .5) / Rows);
    public float TimeScale => (float)(Seconds * rate / Rows);
    // Limit sampling to retained row centers, including during startup or a backlog.
    public float AvailableSpan => (float)(Math.Max(0, position - Math.Max(0, newestRow - Rows + 1)) / Rows);
    public long DroppedRows => Interlocked.Read(ref dropped);
    public long LatestCaptureTimestamp { get; private set; }
    long dropped;

    public AudioHistory(int bands, double analysisRate, double seconds = 1, double presentationDelay = .02)
    {
        if (bands <= 0 || analysisRate <= 0 || !double.IsFinite(analysisRate)
            || seconds <= 0 || !double.IsFinite(seconds)
            || presentationDelay < 0 || !double.IsFinite(presentationDelay)) throw new ArgumentOutOfRangeException();
        Bands = bands; rate = analysisRate; Seconds = seconds;
        PresentationDelay = presentationDelay;
        // History duration and presentation delay both consume ring capacity.
        Rows = (int)Math.Clamp(Math.Ceiling((seconds + presentationDelay) * rate) + 2, 4, 16384);
        Spectrum = [new float[4 * bands], new float[4 * bands]];
        History = [new float[4 * bands * Rows], new float[4 * bands * Rows]];
        peak = new float[8 * bands];
        pending = Channel.CreateBounded<(long, long, long, float[])>(new BoundedChannelOptions(Math.Min(4096, Rows * 2))
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        }, _ => Interlocked.Increment(ref dropped));
    }

    public void Post(SampleEvent<double> input, int channelCount)
    {
        if (input.SampleCount != Bands * 4 || channelCount < 1)
            throw new ArgumentException("Analysis dimensions do not match the renderer.");
        var owned = new float[Bands * 8];
        for (var c = 0; c < 2; c++)
        {
            var src = input.GetChannel(Math.Min(c, channelCount - 1)).Span;
            for (var i = 0; i < src.Length; i++)
                owned[c * Bands * 4 + i] = double.IsFinite(src[i]) ? (float)Math.Clamp(src[i], 0, float.MaxValue) : 0;
        }
        // Audio rows are fixed by sample count; arrival timing is profiling data.
        pending.Writer.TryWrite((input.AnalysisRow, input.CaptureTimestamp, input.AnalysisTimestamp, owned));
    }

    public void Advance(double dt)
    {
        Flash *= (float)Math.Exp(-dt / .09);
        Array.Clear(peak);

        var now = Now;
        while (pending.Reader.TryRead(out var packet))
        {
            if (!anchored)
            {
                anchorTime = now;
                anchorRow = packet.Row;
                anchored = true;
            }
            var row = packet.Row;
            LatestCaptureTimestamp = packet.Capture;
            newestRow = Math.Max(newestRow, row);
            // Skipped analysis rows remain empty, including after ring wrap.
            for (var clear = Math.Max(latestRow + 1, row - Rows + 1); clear <= row; clear++)
                for (var c = 0; c < 2; c++)
                    Array.Clear(History[c], (int)((clear % Rows + Rows) % Rows) * Bands * 4, Bands * 4);
            latestRow = Math.Max(latestRow, row);
            if (row < latestRow - Rows + 1) continue;
            if (packet.Capture != 0) Profile?.Elapsed("render.capture_to_consume_ms", packet.Capture);
            if (packet.Completed != 0) Profile?.Elapsed("render.analysis_to_consume_ms", packet.Completed);
            var offset = (int)((row % Rows + Rows) % Rows) * Bands * 4;
            for (var c = 0; c < 2; c++)
                packet.Samples.AsSpan(c * Bands * 4, Bands * 4).CopyTo(History[c].AsSpan(offset));
            for (var i = 0; i < peak.Length; i++) peak[i] = Math.Max(peak[i], packet.Samples[i]);
            var power = 0.0;
            for (var i = 0; i < packet.Samples.Length; i += 4)
            {
                // Alpha is the original linear magnitude; RGB are additive amplitude shares.
                var magnitude = (double)packet.Samples[i + 3];
                power += magnitude * magnitude;
                Flash = Math.Max(Flash, packet.Samples[i + 1]);
            }
            Energy = (float)Math.Sqrt(power / (2 * Bands));
            MusicalTime += power / (2 * Bands * rate);
            lastAudio = now;

        }
        // Maximum over exactly the events drained since the previous frame.
        for (var c = 0; c < 2; c++) peak.AsSpan(c * Bands * 4, Bands * 4).CopyTo(Spectrum[c]);
        if (anchored)
        {
            var desired = anchorRow + (now - anchorTime - PresentationDelay) * rate;
            // If a burst exceeds the delay reserve, move forward enough to retain the visible window.
            var minimum = Math.Min(newestRow, Math.Max(0, newestRow - Rows + 1 + Seconds * rate));
            position = Math.Clamp(desired, minimum, newestRow);
            if (desired > newestRow) Profile?.Record("render.presentation_stall_ms", (desired - newestRow) * 1000 / rate);
        }
        if (now - lastAudio > .15)
        {
            foreach (var spectrum in Spectrum) Array.Clear(spectrum);
            Energy = 0;
        }
    }
}


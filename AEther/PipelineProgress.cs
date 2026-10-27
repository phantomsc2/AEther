using System;
using System.Collections.Generic;
using System.Threading;

namespace AEther;

/// <summary>An exclusive device-frame position in one measurement epoch. Never derived from callback arrival times.</summary>
public readonly record struct SourceWatermark(long Epoch, long Frame, long Segment = 0);

/// <summary>Source seconds advanced per elapsed second. Null means no comparable pair of observations.</summary>
public readonly record struct ThroughputSample(long Timestamp, double? Capture, double? Analysis, double? Render, double? Gpu);

/// <summary>Independent checkpoint rates, observed every 100 ms without smoothing.</summary>
public sealed class PipelineProgress : IDisposable
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);
    readonly object gate = new();
    readonly ThroughputSample[] history = new ThroughputSample[256];
    readonly Observation[] previous = new Observation[4];
    readonly int sampleRate;
    readonly TimeProvider clock;
    ITimer? timer;
    long epoch = 1, segment, count;
    long? captured, analyzed, submitted, completed;
    bool canvasActive;
    ThroughputSample latest;
    readonly record struct Observation(long? Frame, long Timestamp, long Segment);

    public PipelineProgress(int sampleRate, TimeProvider? clock = null)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.sampleRate = sampleRate;
        this.clock = clock ?? TimeProvider.System;
    }

    public ThroughputSample Latest { get { lock (gate) return latest; } }

    public void Start()
    {
        lock (gate) timer ??= clock.CreateTimer(_ => Sample(), null, TimeSpan.Zero, SampleInterval);
    }

    public SourceWatermark? Capture(AudioPacketTiming? timing, int frames)
    {
        lock (gate)
        {
            if (frames <= 0) return null;
            if (timing is not { Quality: AudioTimestampQuality.Device, FramePosition: { } start }
                || timing.Value.Flags.HasFlag(AudioPacketFlags.TimestampError) || start < 0)
            {
                if (captured.HasValue) ResetEpoch();
                return null;
            }
            if (captured.HasValue && start < captured.Value) ResetEpoch();
            else if (captured.HasValue && (start != captured.Value || timing.Value.Flags.HasFlag(AudioPacketFlags.Discontinuity))) BreakSegment();
            captured = checked(start + frames);
            return new(epoch, captured.Value, segment);
        }
    }

    public void Analyzed(SourceWatermark? value)
    {
        lock (gate)
            if (value is { } mark && Valid(mark) && (!analyzed.HasValue || mark.Frame > analyzed.Value)) analyzed = mark.Frame;
    }

    public void SetCanvasActive(bool active)
    {
        lock (gate)
        {
            if (canvasActive == active) return;
            canvasActive = active;
            submitted = completed = null;
            previous[2] = previous[3] = default;
        }
    }

    public void Submitted(SourceWatermark value)
    {
        lock (gate)
            if (canvasActive && Valid(value) && (!submitted.HasValue || value.Frame > submitted.Value)) submitted = value.Frame;
    }

    public void Completed(SourceWatermark value)
    {
        lock (gate)
            if (canvasActive && Valid(value) && value.Frame <= submitted && (!completed.HasValue || value.Frame > completed.Value)) completed = value.Frame;
    }

    public void MarkGap() { lock (gate) BreakSegment(); }

    bool Valid(SourceWatermark? value) => captured.HasValue && value.HasValue && value.Value.Epoch == epoch && value.Value.Segment == segment
        && value.Value.Frame >= 0 && value.Value.Frame <= captured.Value;

    void ResetEpoch()
    {
        epoch++;
        captured = null;
        BreakSegment();
    }

    void BreakSegment()
    {
        segment++;
        analyzed = submitted = completed = null;
    }

    /// <summary>Observe all checkpoints together. Timer delays use actual elapsed time, never an assumed 100 ms.</summary>
    public ThroughputSample Sample()
    {
        lock (gate)
        {
            long now = AudioClock.Now(clock);
            if (count != 0 && now == latest.Timestamp) return latest;
            if (count != 0 && now < latest.Timestamp) segment++;
            double? Rate(long? frame, int index)
            {
                var before = previous[index];
                previous[index] = new(frame, now, segment);
                if (!frame.HasValue || !before.Frame.HasValue || before.Segment != segment
                    || frame.Value < before.Frame.Value || now <= before.Timestamp) return null;
                return (frame.Value - before.Frame.Value) / (sampleRate * AudioClock.Seconds(now - before.Timestamp));
            }
            latest = new(now, Rate(captured, 0), Rate(analyzed, 1),
                Rate(canvasActive ? submitted : null, 2), Rate(canvasActive ? completed : null, 3));
            history[count++ % history.Length] = latest;
            return latest;
        }
    }

    public long ReadSince(ref long cursor, List<ThroughputSample> destination)
    {
        lock (gate)
        {
            destination.Clear();
            long first = Math.Max(0, count - history.Length), missed = Math.Max(0, first - cursor);
            for (long i = Math.Max(first, cursor); i < count; i++) destination.Add(history[i % history.Length]);
            cursor = count;
            return missed;
        }
    }

    public void Dispose()
    {
        ITimer? timerToDispose;
        lock (gate) { timerToDispose = timer; timer = null; }
        timerToDispose?.Dispose();
    }
}

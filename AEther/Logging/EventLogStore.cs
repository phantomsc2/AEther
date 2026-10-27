using System;
using System.Collections.Generic;
using Serilog.Core;
using Serilog.Events;

namespace AEther.Logging;

public enum IndicatorState { Neutral, Warning, Error }

public sealed record AppEvent(long Sequence, LogEvent Event);
public sealed record EventLogSnapshot(long Version, IndicatorState State, AppEvent[] Events);

/// <summary>Bounded application history and sticky health, independent of XAML and file I/O.</summary>
public sealed class EventLogStore : ILogEventSink
{
    readonly object gate = new();
    readonly Queue<AppEvent> events = new();
    readonly int capacity;
    long sequence, version, nextPipeline, activePipeline, pendingPipeline;
    IndicatorState state, pendingState;

    public EventLogStore(int capacity = 2000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public long BeginPipeline()
    {
        lock (gate)
        {
            pendingPipeline = ++nextPipeline;
            pendingState = IndicatorState.Neutral;
            return pendingPipeline;
        }
    }

    public void PipelineStarted(long id)
    {
        lock (gate)
        {
            if (id == 0 || id != pendingPipeline) return;
            activePipeline = id;
            pendingPipeline = 0;
            state = pendingState;
            version++;
        }
    }

    public void PipelineFailed(long id)
    {
        lock (gate)
            if (id == pendingPipeline) pendingPipeline = 0;
    }

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;
        lock (gate)
        {
            if (events.Count == capacity) events.Dequeue();
            events.Enqueue(new(++sequence, logEvent));
            version++;
            if (logEvent.Level < LogEventLevel.Warning) return;
            long? pipeline = logEvent.Properties.TryGetValue("PipelineId", out var value)
                && value is ScalarValue { Value: long id } ? id : null;
            // Retired callbacks remain useful history, but cannot relight a new generation.
            if (pipeline.HasValue && pipeline != activePipeline && pipeline != pendingPipeline) return;
            var severity = logEvent.Level >= LogEventLevel.Error
                && logEvent.Properties.TryGetValue("PipelineBlocking", out var blocking)
                && blocking is ScalarValue { Value: true } ? IndicatorState.Error : IndicatorState.Warning;
            state = (IndicatorState)Math.Max((int)state, (int)severity);
            if (pendingPipeline != 0 && (!pipeline.HasValue || pipeline == pendingPipeline))
                pendingState = (IndicatorState)Math.Max((int)pendingState, (int)severity);
        }
    }

    public EventLogSnapshot Snapshot()
    {
        lock (gate) return new(version, state, events.ToArray());
    }

    public EventLogSnapshot? ChangesSince(long previousVersion)
    {
        lock (gate) return version == previousVersion ? null : new(version, state, events.ToArray());
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using Serilog.Core;
using Serilog.Events;

namespace AEther.Logging;

public sealed record AppEvent(long Sequence, LogEvent Event);
public sealed record EventLogSnapshot(long Version, AppEvent[] Events);

/// <summary>Bounded application event history, independent of XAML and file I/O.</summary>
public sealed class EventLogStore : ILogEventSink
{
    readonly object gate = new();
    readonly Queue<AppEvent> events = new();
    readonly int capacity;
    long sequence, version, nextPipeline;

    public EventLogStore(int capacity = 2000)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public long BeginPipeline() => Interlocked.Increment(ref nextPipeline);

    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Debug) return;
        lock (gate)
        {
            if (events.Count == capacity) events.Dequeue();
            events.Enqueue(new(++sequence, logEvent));
            version++;
        }
    }

    public EventLogSnapshot Snapshot()
    {
        lock (gate) return new(version, events.ToArray());
    }

    public EventLogSnapshot? ChangesSince(long previousVersion)
    {
        lock (gate) return version == previousVersion ? null : new(version, events.ToArray());
    }
}

using System;
using System.Collections.Generic;

namespace AEther;

/// <summary>A bounded metric history with independent reader cursors. Rendering occurs after copying out of its short lock.</summary>
public sealed class TimingHistory
{
    readonly TimingSample[] samples;
    readonly object gate = new();
    long count;

    public TimingHistory(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        samples = new TimingSample[capacity];
    }

    internal void Record(long timestamp, double milliseconds)
    {
        lock (gate)
        {
            samples[count % samples.Length] = new(timestamp, milliseconds);
            count++;
        }
    }

    /// <summary>Copy new samples in order into a reusable list. Returns samples overwritten since the previous read.</summary>
    public long ReadSince(ref long cursor, List<TimingSample> destination)
    {
        destination.Clear();
        lock (gate)
        {
            var first = Math.Max(0, count - samples.Length);
            var missed = Math.Max(0, first - cursor);
            for (var i = Math.Max(first, cursor); i < count; i++)
                destination.Add(samples[i % samples.Length]);
            cursor = count;
            return missed;
        }
    }
}

/// <param name="Timestamp">Monotonic Stopwatch ticks at the measurement endpoint.</param>
public readonly record struct TimingSample(long Timestamp, double Milliseconds);

using System;
using System.Diagnostics;

namespace AEther;

public enum AudioTimestampQuality { Unspecified, Estimated, Device }

[Flags]
public enum AudioPacketFlags { None = 0, Silent = 1, Discontinuity = 2, TimestampError = 4 }

/// <summary>Timing of the first interleaved frame. Timestamps use Stopwatch ticks, not wall time.</summary>
public readonly record struct AudioPacketTiming(long? Timestamp = null, long? FramePosition = null,
    AudioTimestampQuality Quality = AudioTimestampQuality.Unspecified, AudioPacketFlags Flags = AudioPacketFlags.None);

public delegate void TimedSampleDataHandler(object? sender, ReadOnlySpan<byte> data, AudioPacketTiming timing);

/// <summary>Optional live capture capability. Subscribe to this OR SampleSource.DataAvailable.</summary>
public interface ITimedSampleSource
{
    event TimedSampleDataHandler? TimedDataAvailable;
}

/// <summary>One monotonic clock domain shared by capture, analysis and presentation.</summary>
public static class AudioClock
{
    public static long ConvertTimestamp(long value, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        return checked((long)((Int128)value * Stopwatch.Frequency / frequency));
    }

    public static long Now(TimeProvider provider) => ConvertTimestamp(provider.GetTimestamp(), provider.TimestampFrequency);
    public static long Frames(long count, int sampleRate) => ConvertTimestamp(count, sampleRate);
    public static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}

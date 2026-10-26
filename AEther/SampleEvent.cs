using System;

namespace AEther
{
    public record SampleEvent<T>
    (
        T[] Samples,
        int SampleCount,
        DateTime Time
    )
    {
        /// <summary>Optional channel-major residual RMS in linear amplitude units, one value per band.
        /// Like Samples, pooled analyzer storage is valid only during the event callback.</summary>
        public ReadOnlyMemory<double> ResidualRms { get; init; }
        /// <summary>Optional diagnostic plane. Log-polar analysis supplies the fourth
        /// squashed coordinate here for the existing Spectrum ShowR control.
        /// Legacy STBR callers supply A-B-S-T. Callback lifetime matches Samples.</summary>
        public ReadOnlyMemory<double> MomentaryResidual { get; init; }
        /// <summary>Optional coherent observed magnitude before tracking, one value per
        /// band/channel. Used for same-frame raw versus inferred Spectrum diagnostics.</summary>
        public ReadOnlyMemory<double> ObservedAmplitude { get; init; }
        public long SampleIndex { get; init; }
        /// <summary>Device-frame/QPC correlation shared by all rows in this stream epoch.</summary>
        public AudioStreamClock? StreamClock { get; init; }
        public long AnalysisRow { get; init; }
        public long ByteIndex { get; init; }
        public long CaptureTimestamp { get; init; }
        public long AnalysisTimestamp { get; init; }
        /// <summary>Completed source interval [start, end), in Stopwatch ticks. Null for offline/legacy data.</summary>
        public long? SampleStartTimestamp { get; init; }
        public long? SampleEndTimestamp { get; init; }
        public AudioTimestampQuality TimestampQuality { get; init; }
        public long DiscontinuityGeneration { get; init; }

        public Memory<T> GetChannel(int channelIndex)
        {
            var offset = channelIndex * SampleCount;
            return Samples.AsMemory(offset, SampleCount);
        }

    }
}

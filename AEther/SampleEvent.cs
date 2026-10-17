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
        public long SampleIndex { get; init; }
        public long AnalysisRow { get; init; }
        public long ByteIndex { get; init; }
        public long CaptureTimestamp { get; init; }
        public long AnalysisTimestamp { get; init; }
        public GlobalEvidence GlobalEvidence { get; init; }

        public Memory<T> GetChannel(int channelIndex)
        {
            var offset = channelIndex * SampleCount;
            return Samples.AsMemory(offset, SampleCount);
        }

    }
}

using System;
using Serilog;

namespace AEther.Logging;

/// <summary>Samples cumulative loss counters off the audio thread; normal silence is not a warning.</summary>
public sealed class PipelineWarnings(ILogger logger, TimeProvider? clock = null)
{
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    long input, visual;
    double lost;
    DateTimeOffset? lastInput, lastVisual;
    AudioTimestampQuality quality;
    bool haveQuality;

    public void Sample(long inputDrops, double lostMilliseconds, long visualDrops, AudioTimestampQuality timestampQuality)
    {
        var now = clock.GetUtcNow();
        if ((inputDrops > input || lostMilliseconds > lost)
            && (!lastInput.HasValue || now - lastInput.Value >= TimeSpan.FromSeconds(5)))
        {
            logger.Warning("Audio input discarded {InputPackets} packets and {DiscardedMilliseconds} ms since the previous report",
                Math.Max(0, inputDrops - input), Math.Max(0, lostMilliseconds - lost));
            input = inputDrops; lost = lostMilliseconds; lastInput = now;
        }
        if (visualDrops > visual && (!lastVisual.HasValue || now - lastVisual.Value >= TimeSpan.FromSeconds(5)))
        {
            logger.Warning("Visual history discarded {VisualRows} rows since the previous report", visualDrops - visual);
            visual = visualDrops; lastVisual = now;
        }
        if (timestampQuality == AudioTimestampQuality.Unspecified) return;
        if (!haveQuality || timestampQuality != quality)
        {
            if (haveQuality && quality == AudioTimestampQuality.Device && timestampQuality != AudioTimestampQuality.Device)
                logger.Warning("Audio timestamp quality degraded from {PreviousQuality} to {TimestampQuality}", quality, timestampQuality);
            else logger.Information("Audio timestamp quality is {TimestampQuality}", timestampQuality);
            quality = timestampQuality; haveQuality = true;
        }
    }
}

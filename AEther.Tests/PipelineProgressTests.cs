using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AEther.Tests;

public class PipelineProgressTests
{
    sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    static AudioPacketTiming Packet(long start, AudioPacketFlags flags = AudioPacketFlags.None) =>
        new(FramePosition: start, Quality: AudioTimestampQuality.Device, Flags: flags);

    static SourceWatermark Publish(PipelineProgress progress, long start, int frames)
    {
        var mark = progress.Capture(Packet(start), frames)!.Value;
        progress.Analyzed(mark);
        progress.Submitted(mark);
        progress.Completed(mark);
        return mark;
    }

    [Test]
    public void CheckpointsMeasureTheirOwnSourceProgress()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        progress.SetCanvasActive(true);
        var mark = progress.Capture(Packet(0), 1000)!.Value;
        progress.Analyzed(mark with { Frame = 900 });
        progress.Submitted(mark with { Frame = 800 });
        progress.Completed(mark with { Frame = 700 });
        progress.Sample();
        clock.Milliseconds = 100;
        mark = progress.Capture(Packet(1000), 100)!.Value;
        progress.Analyzed(mark with { Frame = 980 });
        progress.Submitted(mark with { Frame = 860 });
        progress.Completed(mark with { Frame = 740 });
        var sample = progress.Sample();
        Assert.That(sample.Capture, Is.EqualTo(1).Within(1e-9));
        Assert.That(sample.Analysis, Is.EqualTo(.8).Within(1e-9));
        Assert.That(sample.Render, Is.EqualTo(.6).Within(1e-9));
        Assert.That(sample.Gpu, Is.EqualTo(.4).Within(1e-9));
    }

    [Test]
    public void LateSamplingUsesActualElapsedTime()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(48000, clock);
        progress.SetCanvasActive(true);
        Publish(progress, 0, 4800);
        progress.Sample();
        clock.Milliseconds = 250;
        Publish(progress, 4800, 12000);
        var sample = progress.Sample();
        Assert.That(new[] { sample.Capture, sample.Analysis, sample.Render, sample.Gpu },
            Is.All.EqualTo(1).Within(1e-9));
    }

    [Test]
    public void StallCatchupAndNormalProgressRemainRaw()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        var mark = progress.Capture(Packet(0), 1000)!.Value;
        progress.Analyzed(mark);
        progress.Sample();
        clock.Milliseconds = 100;
        progress.Capture(Packet(1000), 100);
        var stalled = progress.Sample();
        Assert.That(stalled.Capture, Is.EqualTo(1));
        Assert.That(stalled.Analysis, Is.Zero);
        clock.Milliseconds = 200;
        mark = progress.Capture(Packet(1100), 100)!.Value;
        progress.Analyzed(mark);
        Assert.That(progress.Sample().Analysis, Is.EqualTo(2));
        clock.Milliseconds = 300;
        mark = progress.Capture(Packet(1200), 100)!.Value;
        progress.Analyzed(mark);
        Assert.That(progress.Sample().Analysis, Is.EqualTo(1));
    }

    [Test]
    public void PacketSizesAndSilentPacketsDoNotChangeTheRate()
    {
        var clock = new Clock();
        using var small = new PipelineProgress(48000, clock);
        using var large = new PipelineProgress(48000, clock);
        var first = small.Capture(Packet(0), 4800)!.Value;
        small.Analyzed(first);
        large.Analyzed(large.Capture(Packet(0), 4800));
        small.Sample(); large.Sample();
        clock.Milliseconds = 100;
        for (int i = 0; i < 10; i++)
            small.Analyzed(small.Capture(Packet(4800 + i * 480, AudioPacketFlags.Silent), 480));
        large.Analyzed(large.Capture(Packet(4800), 4800));
        Assert.That(small.Sample().Analysis, Is.EqualTo(large.Sample().Analysis).And.EqualTo(1));
    }

    [TestCase(false), TestCase(true)]
    public void GapsDiscardCrossBoundaryRatesAndRejectOldDownstreamResults(bool applicationDrop)
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        progress.SetCanvasActive(true);
        var old = Publish(progress, 0, 1000);
        progress.Sample();
        clock.Milliseconds = 100;
        if (applicationDrop) progress.MarkGap();
        var mark = progress.Capture(Packet(1200, AudioPacketFlags.Discontinuity), 100)!.Value;
        progress.Analyzed(old);
        progress.Completed(old);
        var gap = progress.Sample();
        Assert.That(new[] { gap.Capture, gap.Analysis, gap.Render, gap.Gpu }, Is.All.Null);
        progress.Analyzed(mark);
        progress.Submitted(mark);
        progress.Completed(mark);
        clock.Milliseconds = 200;
        mark = Publish(progress, 1300, 100);
        var baseline = progress.Sample();
        Assert.That(baseline.Capture, Is.EqualTo(1));
        Assert.That(baseline.Analysis, Is.Null, "Old positions must not turn dropped data into catch-up.");
        clock.Milliseconds = 300;
        Publish(progress, 1400, 100);
        var resumed = progress.Sample();
        Assert.That(new[] { resumed.Capture, resumed.Analysis, resumed.Render, resumed.Gpu }, Is.All.EqualTo(1));
        Assert.That(mark.Epoch, Is.EqualTo(old.Epoch), "A gap need not reset the source clock.");
    }

    [Test]
    public void InvalidTimestampsAndClockResetsStartNewBaselines()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        var old = Publish(progress, 100, 100);
        progress.Sample();
        clock.Milliseconds = 100;
        Assert.That(progress.Capture(Packet(200, AudioPacketFlags.TimestampError), 100), Is.Null);
        progress.Analyzed(old);
        Assert.That(progress.Sample().Analysis, Is.Null);
        clock.Milliseconds = 200;
        var next = Publish(progress, 300, 100);
        Assert.That(next.Epoch, Is.Not.EqualTo(old.Epoch));
        Assert.That(progress.Sample().Analysis, Is.Null);
        clock.Milliseconds = 300;
        var reset = Publish(progress, 0, 100);
        Assert.That(reset.Epoch, Is.Not.EqualTo(next.Epoch));
        Assert.That(progress.Sample().Capture, Is.Null);
    }

    [Test]
    public void MissingAndEstimatedPositionsAreUnavailableRatherThanZero()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        progress.Sample();
        clock.Milliseconds = 100;
        Assert.That(progress.Capture(new AudioPacketTiming(Quality: AudioTimestampQuality.Estimated), 100), Is.Null);
        var missing = progress.Sample();
        Assert.That(new[] { missing.Capture, missing.Analysis, missing.Render, missing.Gpu }, Is.All.Null);
        clock.Milliseconds = 200;
        progress.Capture(Packet(0), 100);
        Assert.That(progress.Sample().Capture, Is.Null, "The first observation is a baseline, not a rate.");
        clock.Milliseconds = 300;
        progress.Capture(Packet(100), 100);
        var sample = progress.Sample();
        Assert.That(sample.Capture, Is.EqualTo(1));
        Assert.That(sample.Analysis, Is.Null);
    }

    [Test]
    public void HiddenCanvasDoesNotResetCaptureAndAnalysisRates()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        progress.SetCanvasActive(true);
        Publish(progress, 0, 1000);
        progress.Sample();
        progress.SetCanvasActive(false);
        clock.Milliseconds = 100;
        Publish(progress, 1000, 100);
        var hidden = progress.Sample();
        Assert.That(hidden.Capture, Is.EqualTo(1));
        Assert.That(hidden.Analysis, Is.EqualTo(1));
        Assert.That(hidden.Render, Is.Null);
        Assert.That(hidden.Gpu, Is.Null);
        progress.SetCanvasActive(true);
        clock.Milliseconds = 200;
        Publish(progress, 1100, 100);
        Assert.That(progress.Sample().Render, Is.Null);
        clock.Milliseconds = 300;
        Publish(progress, 1200, 100);
        Assert.That(progress.Sample().Render, Is.EqualTo(1));
    }

    [Test]
    public void DuplicateAndOutOfOrderCompletionsDoNotRewindProgress()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        progress.SetCanvasActive(true);
        var mark = progress.Capture(Packet(0), 1000)!.Value;
        progress.Submitted(mark);
        progress.Completed(mark with { Frame = 800 });
        progress.Sample();
        clock.Milliseconds = 100;
        mark = progress.Capture(Packet(1000), 100)!.Value;
        progress.Submitted(mark);
        progress.Completed(mark with { Frame = 1000 });
        progress.Completed(mark with { Frame = 900 });
        Assert.That(progress.Sample().Gpu, Is.EqualTo(2));
    }

    [Test]
    public void WatermarkUpdatesDoNotGenerateExtraRatePoints()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        Publish(progress, 0, 1000);
        progress.Sample();
        for (int i = 0; i < 10; i++) Publish(progress, 1000 + i * 10, 10);
        clock.Milliseconds = 100;
        progress.Sample();
        progress.Sample();
        _ = progress.Latest;
        long cursor = 0;
        var samples = new List<ThroughputSample>();
        progress.ReadSince(ref cursor, samples);
        Assert.That(samples, Has.Count.EqualTo(2));
        Assert.That(samples[^1].Capture, Is.EqualTo(1));
    }

    [Test]
    public void HistoryIsBoundedAndReportsOverwrittenObservations()
    {
        var clock = new Clock();
        using var progress = new PipelineProgress(1000, clock);
        for (int i = 0; i < 300; i++)
        {
            clock.Milliseconds = i * 100;
            progress.Capture(Packet(i * 100), 100);
            progress.Sample();
        }
        long cursor = 0;
        var samples = new List<ThroughputSample>();
        Assert.That(progress.ReadSince(ref cursor, samples), Is.EqualTo(44));
        Assert.That(samples, Has.Count.EqualTo(256));
        Assert.That(samples.Select(sample => sample.Capture), Is.All.EqualTo(1));
    }

    [Test]
    public async Task AnalyzerPublishesTrustedDeviceFramesThroughTheHandoff()
    {
        var analyzer = new Analyzer(new(SampleType.Float32.Instance, 48000, 2), new());
        var marks = new List<SourceWatermark?>();
        analyzer.SamplesAnalyzed += (_, sample) => marks.Add(sample.SourceWatermark);
        analyzer.Start();
        var start = AudioClock.Now(TimeProvider.System) - AudioClock.Frames(480, 48000);
        analyzer.PostSampleSpan(new byte[480 * 8], new(start, 0, AudioTimestampQuality.Device));
        analyzer.Stop();
        await analyzer.Completion;
        Assert.That(marks, Has.Count.EqualTo(10));
        Assert.That(marks.Select(mark => mark!.Value.Frame), Is.EqualTo(Enumerable.Range(1, 10).Select(i => i * 48L)));
    }
}

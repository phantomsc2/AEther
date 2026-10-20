using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class AudioTimingTests
{
    sealed class Clock : TimeProvider
    {
        public double Milliseconds = 1000;
        public override long TimestampFrequency => 10_000_000;
        public override long GetTimestamp() => (long)Math.Round(Milliseconds * 10000);
    }
    static long Ms(double value) => AudioClock.ConvertTimestamp((long)Math.Round(value * 10000), 10_000_000);
    static readonly SampleFormat Format = new(SampleType.Float32.Instance, 8000, 1);
    static AnalyzerOptions Options() => new() { MinFrequency = 100, MaxFrequency = 1000, TimeResolution = 100, BufferCapacity = 32 };
    static byte[] Audio(int frames)
    {
        var bytes = new byte[frames * 4];
        for (int i = 0; i < frames; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), (float)(.3 * Math.Sin(i * .17)));
        return bytes;
    }
    static List<SampleEvent<double>> Collect(Analyzer analyzer)
    {
        var result = new List<SampleEvent<double>>();
        analyzer.SamplesAnalyzed += (_, e) => result.Add(e with { Samples = e.Samples.AsSpan(0, e.SampleCount).ToArray() });
        return result;
    }

    [Test]
    public void TimestampScalingUsesTheClockFrequencyWithoutOverflow()
    {
        long native = long.MaxValue / 100;
        var expected = (long)((Int128)native * System.Diagnostics.Stopwatch.Frequency / 10_000_000);
        Assert.That(AudioClock.ConvertTimestamp(native, 10_000_000), Is.EqualTo(expected));
        Assert.That(AudioClock.ConvertTimestamp(0, 10_000_000), Is.Zero);
    }

    [Test]
    public async Task SourceIntervalsAndValuesIgnorePacketBoundariesAndArrivalJitter()
    {
        var clock = new Clock();
        var whole = new Analyzer(Format, Options(), clock);
        var chunks = new Analyzer(Format, Options(), clock);
        var expected = Collect(whole); var actual = Collect(chunks);
        var audio = Audio(240);
        whole.PostSampleSpan(audio, new(Ms(960), 0, AudioTimestampQuality.Device));
        int offset = 0;
        foreach (int frames in new[] { 17, 83, 4, 136 })
        {
            clock.Milliseconds += .1;
            chunks.PostSampleSpan(audio.AsSpan(offset * 4, frames * 4),
                new(Ms(960) + AudioClock.Frames(offset, 8000), offset, AudioTimestampQuality.Device));
            offset += frames;
        }
        whole.Stop(); chunks.Stop();
        await Task.WhenAll(whole.Completion, chunks.Completion);
        Assert.That(actual.Count, Is.EqualTo(3));
        for (int i = 0; i < 3; i++)
        {
            Assert.That(actual[i].Samples, Is.EqualTo(expected[i].Samples));
            Assert.That(actual[i].SampleStartTimestamp, Is.EqualTo(Ms(960 + i * 10)));
            Assert.That(actual[i].SampleEndTimestamp, Is.EqualTo(Ms(970 + i * 10)));
            Assert.That(actual[i].TimestampQuality, Is.EqualTo(AudioTimestampQuality.Device));
        }
    }

    [Test]
    public async Task TimestampErrorExtrapolatesWithoutResettingValidAudio()
    {
        var clock = new Clock();
        var analyzer = new Analyzer(Format, Options(), clock);
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(80), new(Ms(970), 0, AudioTimestampQuality.Device));
        analyzer.PostSampleSpan(Audio(80), new(0, 99999, AudioTimestampQuality.Device, AudioPacketFlags.TimestampError));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Count, Is.EqualTo(2));
        Assert.That(rows[1].SampleStartTimestamp, Is.EqualTo(Ms(980)));
        Assert.That(rows[1].TimestampQuality, Is.EqualTo(AudioTimestampQuality.Estimated));
        Assert.That(rows[1].DiscontinuityGeneration, Is.Zero);
    }

    [Test]
    public async Task DeviceGapResetsDftAndFitsAndSilentFlagOverridesBufferContents()
    {
        var clock = new Clock();
        var analyzer = new Analyzer(Format, Options(), clock);
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(80), new(Ms(960), 0, AudioTimestampQuality.Device));
        analyzer.PostSampleSpan(Audio(80), new(Ms(980), 160, AudioTimestampQuality.Device, AudioPacketFlags.Silent));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Select(r => r.AnalysisRow), Is.EqualTo(new long[] { 0, 2 }));
        Assert.That(rows[1].Samples, Is.All.Zero);
        Assert.That(rows[1].GlobalEvidence.TimeSamples, Is.EqualTo(1));
        Assert.That(rows[1].DiscontinuityGeneration, Is.EqualTo(1));
    }

    [Test]
    public async Task OversizedLivePacketSkipsStalePrefixButOfflineInputIsPreserved()
    {
        var clock = new Clock();
        var live = new Analyzer(Format, Options(), clock);
        var offline = new Analyzer(Format, Options(), clock);
        var rows = Collect(live); var all = Collect(offline);
        live.PostSampleSpan(Audio(800), new(Ms(900), 0, AudioTimestampQuality.Device));
        offline.PostSamples(Audio(800));
        live.Stop(); offline.Stop(); await Task.WhenAll(live.Completion, offline.Completion);
        Assert.That(rows.Count, Is.EqualTo(5));
        Assert.That(rows[0].AnalysisRow, Is.EqualTo(5));
        Assert.That(rows[^1].SampleEndTimestamp, Is.EqualTo(Ms(1000)));
        Assert.That(live.DroppedAudioMilliseconds, Is.EqualTo(50));
        Assert.That(all.Count, Is.EqualTo(10));
    }

    [Test]
    public async Task LiveAgeIsRecheckedBetweenBatches()
    {
        var clock = new Clock();
        var analyzer = new Analyzer(Format, Options(), clock);
        var rows = Collect(analyzer);
        analyzer.SamplesAnalyzed += (_, _) => clock.Milliseconds = 1040;
        analyzer.PostSampleSpan(Audio(320), new(Ms(950), 0, AudioTimestampQuality.Device));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Count, Is.EqualTo(1));
        Assert.That(analyzer.DroppedAudioMilliseconds, Is.EqualTo(30));
    }

    [Test]
    public async Task FreshCoarseTickKeepsItsNecessaryOlderSamples()
    {
        var options = Options(); options.TimeResolution = 16;
        var clock = new Clock();
        var analyzer = new Analyzer(Format, options, clock);
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(500), new(Ms(937.5), 0, AudioTimestampQuality.Device));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Count, Is.EqualTo(1), "A fresh 62.5 ms tick must work with the default 50 ms age budget.");
        Assert.That(rows[0].SampleEndTimestamp, Is.EqualTo(Ms(1000)));
        Assert.That(analyzer.DroppedAudioMilliseconds, Is.Zero);
    }

    [Test]
    public async Task AgeCroppingDoesNotDiscardTheFreshPartiallyOverlappingTick()
    {
        var clock = new Clock { Milliseconds = 1005 };
        var analyzer = new Analyzer(Format, Options(), clock);
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(800), new(Ms(900), 0, AudioTimestampQuality.Device));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Count, Is.EqualTo(5));
        Assert.That(rows[0].SampleStartTimestamp, Is.EqualTo(Ms(950)));
        Assert.That(rows[0].SampleEndTimestamp, Is.EqualTo(Ms(960)));
    }

    [TestCase(false), TestCase(true)]
    public async Task PauseResumeStartsFreshWithoutLosingAllFuturePackets(bool deviceTiming)
    {
        var clock = new Clock();
        var analyzer = new Analyzer(Format, Options(), clock);
        var rows = Collect(analyzer);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.SamplesAnalyzed += (_, _) => first.TrySetResult();
        analyzer.Start();
        analyzer.PostSampleSpan(Audio(80), deviceTiming ? new(Ms(990), 0, AudioTimestampQuality.Device) : new());
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Milliseconds = deviceTiming ? 1030 : 1200;
        // Device positions can be consecutive even though packet time advanced while playback was paused.
        analyzer.PostSampleSpan(Audio(80), deviceTiming ? new(Ms(1020), 80, AudioTimestampQuality.Device) : new());
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Count, Is.EqualTo(2));
        Assert.That(rows[1].SampleEndTimestamp, Is.EqualTo(Ms(clock.Milliseconds)));
        Assert.That(rows[1].DiscontinuityGeneration, Is.EqualTo(1));
        Assert.That(rows[1].AnalysisRow, Is.GreaterThan(1));
        Assert.That(rows[1].GlobalEvidence.TimeSamples, Is.EqualTo(1));
    }

    [Test]
    public void ResetMatchesFreshProcessor([Values] DFTWindow window, [Values] bool simd)
    {
        var options = Options();
        var processor = new DFTProcessor(options.Domain, 8000, simd, window);
        var fresh = new DFTProcessor(options.Domain, 8000, simd, window);
        processor.Process(Enumerable.Range(0, 1200).Select(i => Math.Sin(i * .13)).ToArray());
        processor.Reset();
        var input = Enumerable.Range(0, 117).Select(i => Math.Cos(i * .31)).ToArray();
        processor.Process(input); fresh.Process(input);
        var result = new double[options.Domain.Length]; var expected = new double[result.Length];
        processor.OutputLinear(result); fresh.OutputLinear(expected);
        Assert.That(result, Is.EqualTo(expected));
    }

    static SampleEvent<double> Row(long row, double start, double end, double value, long generation = 0) =>
        new(new[] { value, value, value, value }, 4, DateTime.UnixEpoch)
        {
            AnalysisRow = row, SampleStartTimestamp = Ms(start), SampleEndTimestamp = Ms(end),
            TimestampQuality = AudioTimestampQuality.Device, DiscontinuityGeneration = generation,
            GlobalEvidence = new(value, value, value, value, 1, 3),
        };

    [Test]
    public void FrameMeanWeightsDurationsAndPreservesSignedGlobalFields()
    {
        var clock = new Clock { Milliseconds = 20 };
        var history = new AudioHistory(1, 100, timeProvider: clock);
        history.Post(Row(0, 0, 5, 0) with { GlobalEvidence = new(0, .5, 0, 0, 1, 2) }, 1);
        history.Post(Row(1, 5, 20, 1) with { GlobalEvidence = new(1, -.5, 1, 1, 1, 3) }, 1);
        history.Advance(.02);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.75f));
        Assert.That(history.GlobalEvidence.Transient, Is.EqualTo(-.25).Within(1e-12));
        Assert.That(history.GlobalEvidence.TimeSamples, Is.EqualTo(3));
        clock.Milliseconds = 40; history.Advance(.02);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.75f));
        clock.Milliseconds = 71; history.Advance(.031);
        Assert.That(history.Spectrum[0], Is.All.Zero);
        Assert.That(history.GlobalSTBR, Is.EqualTo(System.Numerics.Vector4.Zero));
    }

    [Test]
    public void DelayExcludesFutureRowsAndKeepsHistoryAndLiveOnOneCursor()
    {
        var clock = new Clock { Milliseconds = 20 };
        var history = new AudioHistory(1, 100, presentationDelay: .01, timeProvider: clock);
        history.Post(Row(0, 0, 10, .2), 1); history.Post(Row(1, 10, 20, .8), 1);
        history.Advance(.02);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.2f));
        Assert.That(history.History[0][4], Is.Zero);
        clock.Milliseconds = 30; history.Advance(.01);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.8f));
        Assert.That(history.GlobalSTBR.X, Is.EqualTo(.8f));
        Assert.That(history.NewestAge, Is.Zero);
    }

    [Test]
    public void RendererStallCannotReplayOldAttacksOrRefreshTheirAge()
    {
        var clock = new Clock { Milliseconds = 1000 };
        var history = new AudioHistory(1, 100, timeProvider: clock);
        for (int i = 0; i < 100; i++) history.Post(Row(i, i * 10, (i + 1) * 10, i < 90 ? 1 : 0), 1);
        history.Advance(1);
        Assert.That(history.DroppedRows, Is.EqualTo(90));
        Assert.That(history.Spectrum[0], Is.All.Zero);
        Assert.That(history.Flash, Is.Zero);
        Assert.That(history.MusicalVelocity, Is.Zero);
        clock.Milliseconds = 2000;
        history.Post(Row(100, 1000, 1010, 1), 1); history.Advance(1);
        Assert.That(history.Spectrum[0], Is.All.Zero);
        Assert.That(history.Flash, Is.Zero);
    }

    [Test]
    public void FractionalOverlapAndMissingIntervalsHaveCorrectWeights()
    {
        var clock = new Clock { Milliseconds = 10 };
        var history = new AudioHistory(1, 100, timeProvider: clock);
        history.Post(Row(0, 0, 10, .1), 1); history.Advance(.01);
        clock.Milliseconds = 65;
        history.Post(Row(1, 10, 30, .2), 1);
        history.Post(Row(3, 40, 65, .8), 1);
        history.Advance(.055);
        // Latest 50 ms: 15ms at .2, 10ms missing, 25ms at .8.
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.46f).Within(1e-7));
        Assert.That(history.History[0].AsSpan(8, 4).ToArray(), Is.All.Zero);
    }

    [Test]
    public void NewGenerationCancelsRetentionBeforeFutureDataBecomesDue()
    {
        var clock = new Clock { Milliseconds = 10 };
        var history = new AudioHistory(1, 100, timeProvider: clock);
        history.Post(Row(0, 0, 10, 1), 1); history.Advance(.01);
        history.Post(Row(2, 20, 30, .2, 1), 1); history.Advance(0);
        Assert.That(history.Spectrum[0], Is.All.Zero);
        clock.Milliseconds = 30; history.Advance(.02);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.2f));
    }
}

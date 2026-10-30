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
        public double ReadStep;
        public override long TimestampFrequency => 10_000_000;
        public override long GetTimestamp()
        {
            var timestamp = (long)Math.Round(Milliseconds * 10000);
            Milliseconds += ReadStep;
            return timestamp;
        }
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
    public async Task NativeProcessingAndQueueSamplesUseHostClockBeforeSubscribersWithoutRenderer()
    {
        var clock = new Clock();
        var analyzer = new Analyzer(Format, Options(), clock);
        var processing = analyzer.Profile.Track("analysis.processing_ms");
        var queue = analyzer.Profile.Track("capture.queue_ms");
        var completed = new List<long>();
        analyzer.SamplesAnalyzed += (_, output) =>
        {
            completed.Add(output.AnalysisTimestamp);
            clock.Milliseconds += 100; // Slow subscriber must not inflate batch processing.
        };
        analyzer.PostSampleSpan(Audio(80));
        analyzer.PostSampleSpan(Audio(160));
        clock.Milliseconds = 1050;
        clock.ReadStep = .25;
        analyzer.Stop();
        await analyzer.Completion;
        long cursor = 0;
        var samples = new List<TimingSample>();
        processing.ReadSince(ref cursor, samples);
        Assert.That(samples, Has.Count.EqualTo(3), "One sample per analysis batch, without a renderer.");
        Assert.That(samples.Select(sample => sample.Timestamp), Is.EqualTo(completed));
        Assert.That(samples.Select(sample => sample.Milliseconds), Is.All.InRange(.25, 10));
        cursor = 0;
        queue.ReadSince(ref cursor, samples);
        Assert.That(samples, Has.Count.EqualTo(2), "One sample per input packet.");
        Assert.That(samples.Select(sample => sample.Milliseconds),
            Is.EqualTo(samples.Select(sample => AudioClock.Seconds(sample.Timestamp - Ms(1000)) * 1000)).Within(.001));
        Assert.That(samples[1].Milliseconds, Is.GreaterThan(samples[0].Milliseconds + 100));
    }

    [Test]
    public async Task InputAgeUsesPacketEndAtArrivalBeforeAnalysis()
    {
        var clock = new Clock();
        var analyzer = new Analyzer(Format, Options(), clock);
        var history = analyzer.Profile.Track("capture.input_age_ms");
        analyzer.PostSampleSpan(Audio(80), new(Ms(985), 0, AudioTimestampQuality.Device));
        analyzer.PostSampleSpan(Audio(160), new(Ms(975), 80, AudioTimestampQuality.Device));
        analyzer.PostSampleSpan(Audio(80), new(Ms(985), 240, AudioTimestampQuality.Device, AudioPacketFlags.TimestampError));
        analyzer.PostSampleSpan(Audio(80), new(Ms(985), 320, AudioTimestampQuality.Estimated));
        analyzer.PostSampleSpan(Audio(80));
        clock.Milliseconds += 100;
        analyzer.Stop();
        await analyzer.Completion;
        long cursor = 0;
        var samples = new List<TimingSample>();
        history.ReadSince(ref cursor, samples);
        Assert.That(samples.Count, Is.EqualTo(2));
        Assert.That(samples.Select(sample => sample.Milliseconds), Is.All.EqualTo(5).Within(.001));
        Assert.That(samples.Select(sample => sample.Timestamp), Is.All.EqualTo(Ms(1000)));
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

    [TestCase(-.01)]
    [TestCase(.01)]
    public async Task DeviceClockDriftKeepsFramesOnTheFixedStreamClock(double driftMs)
    {
        var clock = new Clock();
        var analyzer = new Analyzer(Format, Options(), clock);
        var rows = Collect(analyzer);
        for (int i = 0; i < 3; i++)
            analyzer.PostSampleSpan(Audio(80), new(Ms(960 + i * (10 + driftMs)), i * 80,
                AudioTimestampQuality.Device));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Count, Is.EqualTo(3));
        for (int i = 0; i < rows.Count; i++)
        {
            long start = Ms(960 + i * 10);
            Assert.That(rows[i].SampleStartTimestamp, Is.EqualTo(start));
            Assert.That(rows[i].SampleEndTimestamp, Is.EqualTo(start + AudioClock.Frames(80, 8000)));
            Assert.That(rows[i].TimestampQuality, Is.EqualTo(AudioTimestampQuality.Device));
            Assert.That(rows[i].DiscontinuityGeneration, Is.Zero);
        }
        Assert.That(analyzer.LastTimestampQuality, Is.EqualTo(AudioTimestampQuality.Device));
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
    public async Task NonzeroDevicePositionAlignsBatchesToDriverFrames()
    {
        var analyzer = new Analyzer(Format, Options(), new Clock());
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(303), new(Ms(960), 817, AudioTimestampQuality.Device));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Select(r => r.SampleIndex), Is.EqualTo(new long[] { 880, 960, 1040 }));
        Assert.That(rows.Select(r => r.AnalysisRow), Is.EqualTo(new long[] { 11, 12, 13 }));
        Assert.That(rows[0].SampleStartTimestamp, Is.EqualTo(Ms(967.875)));
        Assert.That(rows.Select(r => r.StreamClock), Is.All.EqualTo(new AudioStreamClock(817, Ms(960), 8000)));
        Assert.That(analyzer.DroppedAudioMilliseconds, Is.EqualTo(7.875));
    }

    [Test]
    public async Task TimestampStepsCannotInsertFramesOrResetSignalProcessing()
    {
        var reference = new Analyzer(Format, Options(), new Clock());
        var analyzer = new Analyzer(Format, Options(), new Clock());
        var expected = Collect(reference); var actual = Collect(analyzer);
        var audio = Audio(240);
        reference.PostSampleSpan(audio, new(Ms(960), 800, AudioTimestampQuality.Device));
        double[] stamps = { 960, 973, 978 };
        for (int i = 0; i < 3; i++)
            analyzer.PostSampleSpan(audio.AsSpan(i * 80 * 4, 80 * 4),
                new(Ms(stamps[i]), 800 + i * 80, AudioTimestampQuality.Device));
        reference.Stop(); analyzer.Stop(); await Task.WhenAll(reference.Completion, analyzer.Completion);
        Assert.That(actual.Select(r => r.SampleIndex), Is.EqualTo(new long[] { 800, 880, 960 }));
        Assert.That(actual.Select(r => r.DiscontinuityGeneration), Is.All.Zero);
        Assert.That(actual.Select(r => r.SampleStartTimestamp), Is.EqualTo(new long?[] { Ms(960), Ms(970), Ms(980) }));
        Assert.That(analyzer.DroppedAudioMilliseconds, Is.Zero);
        for (int i = 0; i < actual.Count; i++) Assert.That(actual[i].Samples, Is.EqualTo(expected[i].Samples));
        Assert.That(actual.Select(r => r.StreamClock), Is.All.EqualTo(new AudioStreamClock(800, Ms(960), 8000)));
    }

    [Test]
    public async Task DeviceGapDiscardsPartialBatchAndLeavesTheDriverGapIntact()
    {
        var analyzer = new Analyzer(Format, Options(), new Clock());
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(40), new(Ms(960), 0, AudioTimestampQuality.Device));
        analyzer.PostSampleSpan(Audio(80), new(Ms(980), 160, AudioTimestampQuality.Device));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Select(r => r.SampleIndex), Is.EqualTo(new long[] { 160 }));
        Assert.That(rows[0].DiscontinuityGeneration, Is.EqualTo(1));
        Assert.That(analyzer.DroppedAudioMilliseconds, Is.EqualTo(20));
    }

    [Test]
    public async Task InvalidPacketPositionsUseOnlyTheMissingPacketsSampleCount()
    {
        var analyzer = new Analyzer(Format, Options(), new Clock());
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(80), new(Ms(960), 800, AudioTimestampQuality.Device));
        analyzer.PostSampleSpan(Audio(80), new(0, 99999, AudioTimestampQuality.Device, AudioPacketFlags.TimestampError));
        analyzer.PostSampleSpan(Audio(80), new(Ms(980), 960, AudioTimestampQuality.Device));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Select(r => r.SampleIndex), Is.EqualTo(new long[] { 800, 880, 960 }));
        Assert.That(rows.Select(r => r.TimestampQuality), Is.EqualTo(new[] {
            AudioTimestampQuality.Device, AudioTimestampQuality.Estimated, AudioTimestampQuality.Device }));
        Assert.That(rows.Select(r => r.DiscontinuityGeneration), Is.All.Zero);
        Assert.That(rows.Select(r => r.StreamClock), Is.All.EqualTo(rows[0].StreamClock));
    }

    [Test]
    public async Task DeviceResetStartsANewPositionAndClockEpoch()
    {
        var analyzer = new Analyzer(Format, Options(), new Clock());
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(80), new(Ms(960), 800, AudioTimestampQuality.Device));
        analyzer.PostSampleSpan(Audio(80), new(Ms(980), 0, AudioTimestampQuality.Device, AudioPacketFlags.Discontinuity));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Select(r => r.SampleIndex), Is.EqualTo(new long[] { 800, 0 }));
        Assert.That(rows[1].DiscontinuityGeneration, Is.EqualTo(1));
        Assert.That(rows[1].StreamClock, Is.EqualTo(new AudioStreamClock(0, Ms(980), 8000)));
    }

    [Test]
    public async Task ExplicitDiscontinuityReanchorsClockWithoutInsertingFrames()
    {
        var analyzer = new Analyzer(Format, Options(), new Clock());
        var rows = Collect(analyzer);
        analyzer.PostSampleSpan(Audio(80), new(Ms(960), 800, AudioTimestampQuality.Device));
        analyzer.PostSampleSpan(Audio(80), new(Ms(990), 880, AudioTimestampQuality.Device, AudioPacketFlags.Discontinuity));
        analyzer.Stop(); await analyzer.Completion;
        Assert.That(rows.Select(r => r.SampleIndex), Is.EqualTo(new long[] { 800, 880 }));
        Assert.That(rows[1].DiscontinuityGeneration, Is.EqualTo(1));
        Assert.That(rows[1].StreamClock, Is.EqualTo(new AudioStreamClock(880, Ms(990), 8000)));
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
        Assert.That(rows[1].SampleEndTimestamp, Is.EqualTo(Ms(deviceTiming ? 1010 : clock.Milliseconds)));
        Assert.That(rows[1].DiscontinuityGeneration, Is.EqualTo(deviceTiming ? 0 : 1));
        Assert.That(rows[1].AnalysisRow, Is.EqualTo(1), "Elapsed host time must not invent device frames.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LongCapturePauseReanchorsConsecutiveFramesAndRestoresSpectrum(bool timestampError)
    {
        var clock = new Clock();
        var analyzer = new Analyzer(Format, Options(), clock);
        var history = new AudioHistory(analyzer.Domain.Length, 100, timeProvider: clock);
        var rows = Collect(analyzer);
        analyzer.SamplesAnalyzed += (_, row) => history.Post(row, 1);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analyzer.SamplesAnalyzed += (_, _) => first.TrySetResult();
        analyzer.Start();
        analyzer.PostSampleSpan(Audio(80), new(Ms(990), 0, AudioTimestampQuality.Device));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        history.Advance(0);
        Assert.That(history.Spectrum[0].Max(), Is.GreaterThan(0));

        clock.Milliseconds = 4000;
        history.Advance(3);
        Assert.That(history.Spectrum[0], Is.All.Zero);
        analyzer.PostSampleSpan(Audio(80), timestampError
            ? new(0, 99999, AudioTimestampQuality.Device, AudioPacketFlags.TimestampError)
            : new(Ms(3990), 80, AudioTimestampQuality.Device));
        // Keep the following packet fresh too: recovery must survive subsequent packets.
        analyzer.PostSampleSpan(Audio(80), new(Ms(4000), 160, AudioTimestampQuality.Device));
        analyzer.Stop();
        await analyzer.Completion;
        clock.Milliseconds = 4010;
        history.Advance(.01);

        Assert.That(rows.Select(r => r.AnalysisRow), Is.EqualTo(new long[] { 0, 1, 2 }));
        Assert.That(rows[1].SampleStartTimestamp, Is.EqualTo(Ms(3990)));
        Assert.That(rows[2].SampleEndTimestamp, Is.EqualTo(Ms(4010)));
        Assert.That(rows[1].DiscontinuityGeneration, Is.EqualTo(1));
        Assert.That(history.Spectrum[0].Max(), Is.GreaterThan(0));
        Assert.That(history.Spectrum[0].Max(), Is.GreaterThan(0));
        Assert.That(history.NewestAge, Is.Zero.Within(1e-6));
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
        };

    [TestCase(0)]
    [TestCase(20)]
    public void LiveHistoryScrollIgnoresBatchedArrivalsTimestampJitterAndRetention(double delayMs)
    {
        var clock = new Clock { Milliseconds = 100 + delayMs };
        var history = new AudioHistory(1, 100, presentationDelay: delayMs / 1000, timeProvider: clock);
        history.Post(Row(0, 90, 100, .1), 1);
        Check(100);
        Check(105); // No new audio: keep moving by half a row.
        history.Post(Row(1, 100, 109.9, .2), 1);
        history.Post(Row(2, 109.9, 119.8, .3), 1);
        Check(120); // Batched arrival and clock drift must not reanchor the playhead.
        Check(125);
        Check(180); // Crossing retention must not change scrolling speed.
        history.Post(Row(3, 119.8, 129.7, .4), 1);
        history.Post(Row(4, 129.7, 139.6, .5), 1);
        Check(185); // Late rows fill their slots without pulling the clock backward.
        Assert.That(history.History[0][12], Is.EqualTo(.4f));
        Assert.That(history.History[0][16], Is.EqualTo(.5f));

        void Check(double targetMs)
        {
            clock.Milliseconds = targetMs + delayMs;
            history.Advance(0); // Absolute monotonic clock determines the scroll, not caller dt.
            double row = (targetMs - 100) / 10;
            Assert.That(history.Shift, Is.EqualTo((row + .5) / history.Rows).Within(1e-7),
                $"target {targetMs} ms");
        }
    }

    [TestCase(0), TestCase(.5), TestCase(.75), TestCase(.99), TestCase(1)]
    public void SpectrumPowerMeanWeightsDurationsAndRetainsUntilStale(double a)
    {
        var clock = new Clock { Milliseconds = 20 };
        var history = new AudioHistory(1, 100, timeProvider: clock) { SpectrumAggregation = a };
        history.Post(Row(0, 0, 5, 0), 1);
        history.Post(Row(1, 5, 20, 1), 1);
        history.Advance(.02);
        double expected = Math.Pow(.75, 1 - a);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(expected).Within(1e-7));
        clock.Milliseconds = 40; history.Advance(.02);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(expected).Within(1e-7));
        clock.Milliseconds = 71; history.Advance(.031);
        Assert.That(history.Spectrum[0], Is.All.Zero);
    }

    [TestCase(0), TestCase(.5), TestCase(.9999999999999999), TestCase(1)]
    public void DelayBuffersHistoryAheadOfPresentationWithoutExposingFutureSpectrum(double a)
    {
        var clock = new Clock { Milliseconds = 20 };
        var history = new AudioHistory(1, 100, presentationDelay: .01, timeProvider: clock) { SpectrumAggregation = a };
        history.Post(Row(0, 0, 10, .2), 1); history.Post(Row(1, 10, 20, .8), 1);
        history.Advance(.02);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.2f));
        Assert.That(history.History[0][4], Is.EqualTo(.8f), "Storage is independent of presentation delay.");
        clock.Milliseconds = 30; history.Advance(.01);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.8f));
        Assert.That(history.NewestAge, Is.Zero);
        history.Post(Row(2, 20, 30, .1), 1);
        clock.Milliseconds = 40; history.Advance(.01);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.1f), "Previous-interval peaks must not leak into the new interval.");
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

    [TestCase(0), TestCase(.5), TestCase(.75), TestCase(.99), TestCase(1)]
    public void FractionalOverlapAndMissingIntervalsHaveCorrectWeights(double a)
    {
        var clock = new Clock { Milliseconds = 10 };
        var history = new AudioHistory(1, 100, timeProvider: clock) { SpectrumAggregation = a };
        history.Post(Row(0, 0, 10, .1), 1); history.Advance(.01);
        clock.Milliseconds = 65;
        history.Post(Row(1, 10, 30, .2), 1);
        history.Post(Row(3, 40, 65, .8), 1);
        history.Advance(.055);
        // Latest 50 ms: 15ms at .2, 10ms missing, 25ms at .8.
        double p = 1 / (1 - a);
        double expected = a == 1 ? .8 : Math.Pow(.3 * Math.Pow(.2, p) + .5 * Math.Pow(.8, p), 1 / p);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(expected).Within(1e-7));
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

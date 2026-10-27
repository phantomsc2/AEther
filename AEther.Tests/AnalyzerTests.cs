using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AEther.Tests
{
    public class AnalyzerTests
    {
        static readonly SampleFormat Format = new(SampleType.Float32.Instance, 8000, 2);
        static AnalyzerOptions Options(int capacity = 32) => new()
        {
            MinFrequency = 100, MaxFrequency = 1000, TimeResolution = 100,
            BufferCapacity = capacity, SIMDEnabled = false,
        };

        static byte[] Audio(int frames)
        {
            var samples = new float[2 * frames];
            for (var i = 0; i < frames; ++i)
            {
                samples[2 * i] = (float)(.4 * Math.Sin(2 * Math.PI * 200 * i / Format.SampleRate));
                samples[2 * i + 1] = (float)(.2 * Math.Sin(2 * Math.PI * 500 * i / Format.SampleRate));
            }
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        static List<double[]> Collect(Analyzer analyzer)
        {
            var results = new List<double[]>();
            analyzer.SamplesAnalyzed += (_, evt) =>
                results.Add(evt.Samples.AsSpan(0, evt.SampleCount * Format.ChannelCount).ToArray());
            return results;
        }

        [Test]
        public async Task RunningAnalyzerKeepsItsSettingsUntilRestart()
        {
            var options = Options();
            options.SmoothingEnabled = true;
            var analyzer = new Analyzer(Format, options);
            try
            {
                options.TimeResolution = 1000;
                options.MaxInputAgeMilliseconds = 1;
                options.SmoothingEnabled = false;
                Assert.That(analyzer.Options, Is.Not.SameAs(options));
                Assert.That(analyzer.Options.TimeResolution, Is.EqualTo(100));
                Assert.That(analyzer.Options.MaxInputAgeMilliseconds, Is.EqualTo(50));
                Assert.That(analyzer.Options.SmoothingEnabled, Is.True);
                var results = Collect(analyzer);
                analyzer.PostSamples(Audio(160));
                analyzer.Stop();
                await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(results.Count, Is.EqualTo(2), "The active generation retains its original block size.");
            }
            finally { analyzer.Stop(); await analyzer.Completion; }
        }

        [Test]
        public async Task DroppedCaptureLeavesAnalysisRowsMissing()
        {
            var analyzer = new Analyzer(Format, Options(2));
            var rows = new List<long>();
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            analyzer.SamplesAnalyzed += (_, evt) =>
            {
                rows.Add(evt.AnalysisRow);
                Assert.That(evt.SampleIndex, Is.EqualTo(evt.AnalysisRow * 80));
                if (rows.Count == 2) drained.TrySetResult();
            };
            for (var i = 0; i < 6; i++) analyzer.PostSamples(Audio(80));
            analyzer.Start();
            try
            {
                await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
                analyzer.PostSamples(Audio(80));
            }
            finally
            {
                analyzer.Stop();
                await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.That(rows, Is.EqualTo(new long[] { 4, 5, 6 }), "Overflow keeps the newest queued audio.");
        }

        [Test]
        public async Task ExactBatchPublishesWithoutWaitingForAnotherCallback()
        {
            var analyzer = new Analyzer(Format, Options());
            var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            analyzer.SamplesAnalyzed += (_, evt) => received.TrySetResult(evt.SampleCount);
            try
            {
                analyzer.Start();
                analyzer.PostSamples(Audio(80));
                Assert.That(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(Splitter.OutputStride * analyzer.Domain.Length));
            }
            finally
            {
                analyzer.Stop();
                await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task FragmentedStereoBytesMatchContiguousAudioAndCaptureMemoryIsCopied()
        {
            var bytes = Audio(240);
            var contiguous = new Analyzer(Format, Options());
            var fragmented = new Analyzer(Format, Options(128));
            var expected = Collect(contiguous);
            var actual = Collect(fragmented);
            contiguous.PostSamples(bytes);
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(37, bytes.Length - offset); // deliberately splits floats and stereo frames
                fragmented.PostSampleSpan(bytes.AsSpan(offset, count));
                offset += count;
            }
            Array.Fill(bytes, (byte)255); // the capture device may reuse its buffer immediately
            contiguous.Stop();
            fragmented.Stop();
            await Task.WhenAll(contiguous.Completion, fragmented.Completion).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(actual.Count, Is.EqualTo(3));
            for (var i = 0; i < actual.Count; ++i)
            {
                Assert.That(actual[i], Is.EqualTo(expected[i]));
                Assert.That(actual[i].All(double.IsFinite), Is.True);
                Assert.That(actual[i].Take(actual[i].Length / 2), Is.Not.EqualTo(actual[i].Skip(actual[i].Length / 2)));
            }
        }

        [Test]
        public async Task QueueIsBoundedAndStopDrainsOnlyAcceptedFullBatches()
        {
            var analyzer = new Analyzer(Format, Options(2));
            var results = Collect(analyzer);
            for (var i = 0; i < 6; ++i)
                analyzer.PostSamples(Audio(80));
            Assert.That(analyzer.DroppedInputCount, Is.EqualTo(4));
            analyzer.Stop();
            analyzer.Stop();
            await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(results.Count, Is.EqualTo(2));
            analyzer.PostSamples(Audio(80));
            Assert.That(analyzer.DroppedInputCount, Is.EqualTo(5));
        }

        [Test]
        public async Task PartialTailDoesNotInventAnExtraBatch()
        {
            var analyzer = new Analyzer(Format, Options());
            var results = Collect(analyzer);
            analyzer.PostSamples(Audio(159));
            analyzer.Stop();
            await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(results.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task SubscriberFailureFaultsCompletionAndClosesInput()
        {
            var analyzer = new Analyzer(Format, Options());
            analyzer.SamplesAnalyzed += (_, _) => throw new InvalidOperationException("subscriber failed");
            analyzer.PostSamples(Audio(160));
            analyzer.Stop();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
            analyzer.PostSamples(Audio(80));
            Assert.That(analyzer.DroppedInputCount, Is.EqualTo(1));
        }

        [Test]
        public async Task SmootherFeedsOriginalSplitterAndPublishesTrueResiduals([Values] SmoothingMode mode, [Values] bool enabled,
            [Values(0.0, 3.0, 6.0)] double tilt)
        {
            var options = Options(); options.SmoothingMode = mode; options.SmoothingEnabled = enabled;
            options.FrequencyTiltDbPerOctave = tilt;
            options.MaxFrequency = 3000;
            var analyzer = new Analyzer(Format, options);
            int count = 0;
            var references = Enumerable.Range(0, Format.ChannelCount)
                .Select(_ => new Splitter(analyzer.Domain, options, 100)).ToArray();
            analyzer.SamplesAnalyzed += (_, e) =>
            {
                Assert.That(e.ResidualRms.Length, Is.EqualTo(analyzer.Domain.Length * Format.ChannelCount));
                Assert.That(e.ObservedAmplitude.Length, Is.EqualTo(e.ResidualRms.Length));
                Assert.That(e.LogSmoothedAmplitude.Length, Is.EqualTo(e.ResidualRms.Length));
                Assert.That(e.ComplexSmoothedAmplitude.Length, Is.EqualTo(e.ResidualRms.Length));
                var selected = !enabled ? e.ObservedAmplitude : mode == SmoothingMode.LogAmplitude ? e.LogSmoothedAmplitude : e.ComplexSmoothedAmplitude;
                if (!enabled)
                {
                    Assert.That(e.LogSmoothedAmplitude.ToArray(), Is.EqualTo(e.ObservedAmplitude.ToArray()));
                    Assert.That(e.ComplexSmoothedAmplitude.ToArray(), Is.EqualTo(e.ObservedAmplitude.ToArray()));
                    Assert.That(analyzer.Profile.Recent("analysis.smooth_channel_ms")!.Value.Mean, Is.Zero);
                }
                else Assert.That(analyzer.Profile.Recent("analysis.smooth_channel_ms"), Is.Not.Null);
                var evidence = new JointEvidence[analyzer.Domain.Length * Format.ChannelCount];
                var referenceOutput = new double[Splitter.OutputStride * analyzer.Domain.Length];
                for (int c = 0; c < Format.ChannelCount; c++)
                {
                    references[c].ProcessLinear(selected.Slice(c * analyzer.Domain.Length, analyzer.Domain.Length), referenceOutput);
                    references[c].Estimates.CopyTo(evidence.AsMemory(c * analyzer.Domain.Length, analyzer.Domain.Length));
                }
                for (int c = 0; c < Format.ChannelCount; c++)
                    for (int k = 0; k < analyzer.Domain.Length; k++)
                    {
                        int i = c * analyzer.Domain.Length + k;
                        var fit = evidence[i]; var output = e.GetChannel(c).Span.Slice(4 * k, 4);
                        var frequency = analyzer.Domain.MinValue * Math.Pow(2, k / (double)analyzer.Domain.Resolution);
                        var gain = Math.Pow(10, tilt * Math.Log2(frequency / 1000) / 20);
                        Assert.That(fit.Amplitude, Is.EqualTo(selected.Span[i]));
                        Assert.That(output[0], Is.EqualTo(Math.Max(0, fit.Sine) * gain).Within(1e-12));
                        Assert.That(output[1], Is.EqualTo(Math.Max(0, fit.Transient) * gain).Within(1e-12));
                        Assert.That(output[2], Is.EqualTo(fit.Background * gain).Within(1e-12));
                        Assert.That(output[3], Is.EqualTo(fit.Amplitude * gain).Within(1e-12));
                        Assert.That(e.ResidualRms.Span[i], Is.EqualTo(fit.ResidualRms * gain).Within(1e-12));
                        Assert.That(e.MomentaryResidual.Span[c * analyzer.Domain.Length + k],
                            Is.EqualTo((fit.Amplitude - fit.Background - fit.Sine - fit.Transient) * gain).Within(1e-12));
                    }
                count++;
            };
            analyzer.PostSamples(Audio(160));
            analyzer.Stop();
            await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(count, Is.EqualTo(2));
        }

        [TestCase(false), TestCase(true)]
        public async Task DisabledSmootherMatchesTheOriginalSpectrumToSplitterPipeline(bool swift)
        {
            Assert.That(new AnalyzerOptions().SmoothingEnabled, Is.False);
            var options = Options(); options.SmoothingEnabled = false; options.UseSwift = swift;
            options.FrequencyTiltDbPerOctave = 0;
            var analyzer = new Analyzer(Format, options);
            var actual = Collect(analyzer);
            var bytes = Audio(240);
            analyzer.PostSamples(bytes); analyzer.Stop();
            await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(actual.Count, Is.EqualTo(3));
            for (int c = 0; c < Format.ChannelCount; c++)
            {
                var backend = SpectrumProcessor.Create(options.Domain, Format.SampleRate, options);
                var splitter = new Splitter(options.Domain, options, 100);
                var magnitude = new double[options.Domain.Length];
                var expected = new double[4 * options.Domain.Length];
                for (int row = 0; row < 3; row++)
                {
                    var input = Enumerable.Range(0, 80).Select(i =>
                        (double)BitConverter.ToSingle(bytes, (2 * (row * 80 + i) + c) * sizeof(float))
                        * Math.Pow(10, options.InputGainDb / 20)).ToArray();
                    backend.Process(input); backend.OutputLinear(magnitude); splitter.ProcessLinear(magnitude, expected);
                    Assert.That(actual[row].AsSpan(c * expected.Length, expected.Length).ToArray(), Is.EqualTo(expected));
                }
            }
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void RejectsUnboundedOrInvalidCapacity(int capacity)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, Options(capacity)));

        [Test]
        public async Task SelectedBackendReachesEveryAudioChannel([Values] DFTWindow window, [Values] bool useSwift)
        {
            var options = Options();
            options.WindowFunction = window;
            options.UseSwift = useSwift;
            options.SwiftWidthScale = 2;
            options.SwiftImbalance = .5;
            var analyzer = new Analyzer(Format, options);
            var rows = Collect(analyzer);
            double[] observed = null!;
            analyzer.SamplesAnalyzed += (_, e) => observed = e.ObservedAmplitude.ToArray();
            var bytes = Audio(80);
            analyzer.PostSamples(bytes);
            analyzer.Stop();
            await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(rows.Count, Is.EqualTo(1));
            for (var c = 0; c < Format.ChannelCount; ++c)
            {
                ISpectrumProcessor processor = useSwift
                    ? new SwiftProcessor(options.Domain, Format.SampleRate, false,
                        widthScale: options.SwiftWidthScale, imbalance: options.SwiftImbalance)
                    : new DFTProcessor(options.Domain, Format.SampleRate, false, window);
                var samples = Enumerable.Range(0, 80)
                    .Select(i => (double)BitConverter.ToSingle(bytes, (2 * i + c) * sizeof(float))
                        * Math.Pow(10, options.InputGainDb / 20)).ToArray();
                processor.Process(samples);
                var expected = new double[options.Domain.Length];
                processor.OutputLinear(expected);
                for (var k = 0; k < expected.Length; ++k)
                    Assert.That(observed[c * expected.Length + k],
                        Is.EqualTo(expected[k]).Within(1e-12));
            }
        }

        [Test]
        public void RejectsUnknownWindowBeforeStartingAnalysis()
        {
            var options = Options();
            options.WindowFunction = (DFTWindow)999;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, options));
        }

        [TestCase(-.1), TestCase(6.1), TestCase(double.NaN), TestCase(double.PositiveInfinity)]
        public void RejectsInvalidFrequencyTilt(double tilt)
        {
            Assert.That(new AnalyzerOptions().FrequencyTiltDbPerOctave, Is.EqualTo(3));
            var options = Options();
            options.FrequencyTiltDbPerOctave = tilt;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, options));
        }

        [Test]
        public void RejectsInvalidFilterWindowsAndFrequencies()
        {
            var options = Options();
            options.LogAccelerationIntensity = -1;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, options));
            options = Options();
            options.MaxFrequency = 5000;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, options));
        }
    }
}

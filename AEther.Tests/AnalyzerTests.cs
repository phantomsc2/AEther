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
            Assert.That(rows, Is.EqualTo(new long[] { 0, 1, 6 }));
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
        public async Task AttackArrivesBeforeTonalAnalysis()
        {
            var analyzer = new Analyzer(Format, Options());
            var events = new List<string>();
            analyzer.TransientDetected += (_, strength) =>
            {
                Assert.That(strength, Is.GreaterThan(0));
                events.Add("attack");
            };
            analyzer.SamplesAnalyzed += (_, _) => events.Add("spectrum");
            analyzer.PostSamples(Audio(80));
            analyzer.Stop();
            await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(events, Is.EqualTo(new[] { "attack", "spectrum" }));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void RejectsUnboundedOrInvalidCapacity(int capacity)
            => Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, Options(capacity)));

        [Test]
        public async Task SelectedWindowReachesEveryAudioChannel([Values] DFTWindow window)
        {
            var options = Options();
            options.WindowFunction = window;
            var analyzer = new Analyzer(Format, options);
            var rows = Collect(analyzer);
            var bytes = Audio(80);
            analyzer.PostSamples(bytes);
            analyzer.Stop();
            await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(rows.Count, Is.EqualTo(1));
            for (var c = 0; c < Format.ChannelCount; ++c)
            {
                var processor = new DFTProcessor(options.Domain, Format.SampleRate, false, window);
                var samples = Enumerable.Range(0, 80)
                    .Select(i => (double)BitConverter.ToSingle(bytes, (2 * i + c) * sizeof(float))).ToArray();
                processor.Process(samples);
                var expected = new double[options.Domain.Length];
                processor.Output(expected);
                for (var k = 0; k < expected.Length; ++k)
                    Assert.That(rows[0][c * Splitter.OutputStride * expected.Length + Splitter.OutputStride * k + 3],
                        Is.EqualTo(Math.Pow(10, expected[k])).Within(1e-12));
            }
        }

        [Test]
        public void RejectsUnknownWindowBeforeStartingAnalysis()
        {
            var options = Options();
            options.WindowFunction = (DFTWindow)999;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, options));
        }

        [Test]
        public void RejectsInvalidFilterWindowsAndFrequencies()
        {
            var options = Options();
            options.MedianLength = 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, options));
            options = Options();
            options.MaxFrequency = 5000;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(Format, options));
        }
    }
}


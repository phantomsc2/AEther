using System;
using System.Linq;
using NUnit.Framework;

namespace AEther.Tests
{
    public class MusicalTimeTests
    {
        static AudioFeatureExtractor Extractor(int channels = 1)
            => new(new(SampleType.Float32.Instance, 48000, channels));

        [Test]
        public void IntegratesPowerInAudioSecondsAndDoesNotDependOnDuplicatedChannels()
        {
            var mono = Extractor().Process(new(Enumerable.Repeat(.5, 48000).ToArray(), 48000, DateTime.Now));
            var stereo = Extractor(2).Process(new(Enumerable.Repeat(.5, 96000).ToArray(), 48000, DateTime.Now));
            var louder = Extractor().Process(new(Enumerable.Repeat(1.0, 48000).ToArray(), 48000, DateTime.Now));
            Assert.That(mono.MusicalTime, Is.EqualTo(.25).Within(1e-12));
            Assert.That(stereo.MusicalTime, Is.EqualTo(mono.MusicalTime).Within(1e-12));
            Assert.That(louder.MusicalTime, Is.EqualTo(4 * mono.MusicalTime).Within(1e-12));
        }

        [Test]
        public void AudioChunkBoundariesDoNotChangeTheCumulativeClock()
        {
            var samples = Enumerable.Range(0, 997).Select(i => .4 * Math.Sin(i * .173)).ToArray();
            var expected = Extractor().Process(new(samples, samples.Length, DateTime.Now)).MusicalTime;
            var chunks = Extractor();
            double actual = 0;
            for (var offset = 0; offset < samples.Length; offset += 73)
            {
                var chunk = samples.AsSpan(offset, Math.Min(73, samples.Length - offset)).ToArray();
                actual = chunks.Process(new(chunk, chunk.Length, DateTime.Now)).MusicalTime;
            }
            Assert.That(actual, Is.EqualTo(expected).Within(1e-12));
        }

        [Test]
        public void SilenceEmptyInputAndNonfiniteSamplesDoNotAdvanceOrResetTheClock()
        {
            var extractor = Extractor();
            var start = extractor.Process(new(new[] { .5 }, 1, DateTime.Now)).MusicalTime;
            var silence = extractor.Process(new(new double[48000], 48000, DateTime.Now)).MusicalTime;
            var empty = extractor.Process(new(Array.Empty<double>(), 0, DateTime.Now)).MusicalTime;
            var invalid = extractor.Process(new(new[] { double.NaN, double.PositiveInfinity }, 2, DateTime.Now)).MusicalTime;
            Assert.That(silence, Is.EqualTo(start));
            Assert.That(empty, Is.EqualTo(start));
            Assert.That(invalid, Is.EqualTo(start));
        }
    }
}

using System;
using NUnit.Framework;

namespace AEther.Tests
{
    public class StormTests
    {
        [Test]
        public void ThunderIsImmediateButCameraPositionDoesNotTeleport()
        {
            var storm = new StormResponse();
            storm.Advance(0, default, .2);
            Assert.That(storm.Flash, Is.EqualTo(1));
            Assert.That(storm.Afterglow, Is.EqualTo(1));
            Assert.That(storm.Breath, Is.Zero);
            Assert.That(storm.Distance, Is.Zero);
            storm.Advance(.1, default, 0);
            Assert.That(storm.Breath, Is.GreaterThan(0));
            Assert.That(storm.Distance, Is.GreaterThan(0));
        }

        [Test]
        public void FlashFadesBeforeAfterglowAndMotionRelaxes()
        {
            var storm = new StormResponse();
            storm.Advance(0, default, .2);
            storm.Advance(.5, default, 0);
            Assert.That(storm.Flash, Is.LessThan(.02));
            Assert.That(storm.Afterglow, Is.GreaterThan(.5));
            Assert.That(storm.Breath, Is.GreaterThan(0));
            storm.Advance(10, default, 0);
            Assert.That(storm.Flash, Is.LessThan(1e-8));
            Assert.That(storm.Afterglow, Is.LessThan(.001));
            Assert.That(storm.Breath, Is.LessThan(1e-8));
        }

        [Test]
        public void MoreEnergyMeansMoreMovementAndSilenceExhalesGradually()
        {
            var quiet = new StormResponse();
            var loud = new StormResponse();
            for (var i = 0; i < 300; ++i)
            {
                quiet.Advance(1.0 / 60, new(.025, .02, .01, .01, 0), 0);
                loud.Advance(1.0 / 60, new(.3, .2, .15, .1, 0), 0);
            }
            Assert.That(loud.Speed, Is.GreaterThan(quiet.Speed + 1));
            Assert.That(loud.Distance, Is.GreaterThan(quiet.Distance));
            var energy = loud.Energy;
            loud.Advance(.1, default, 0);
            Assert.That(loud.Energy, Is.GreaterThan(.8 * energy));
            Assert.That(loud.Energy, Is.LessThan(energy));
            loud.Advance(12, default, 0);
            Assert.That(loud.Energy, Is.LessThan(.001));
            Assert.That(loud.Speed, Is.GreaterThan(.01)); // long orbital exhale
            loud.Advance(50, default, 0);
            Assert.That(loud.Speed, Is.LessThan(.01));
        }

        [Test]
        public void EnvelopesAgreeAtDifferentFrameRates()
        {
            var coarse = new StormResponse();
            var fine = new StormResponse();
            var features = new AudioFeatures(.2, .1, .06, .04, 0);
            coarse.Advance(0, features, .2);
            fine.Advance(0, features, .2);
            coarse.Advance(.5, features, 0);
            for (var i = 0; i < 60; ++i) fine.Advance(.5 / 60, features, 0);
            Assert.That(fine.Energy, Is.EqualTo(coarse.Energy).Within(1e-12));
            Assert.That(fine.Flash, Is.EqualTo(coarse.Flash).Within(1e-12));
            Assert.That(fine.Afterglow, Is.EqualTo(coarse.Afterglow).Within(1e-12));
            Assert.That(fine.Breath, Is.EqualTo(coarse.Breath).Within(1e-12));
        }

        [Test]
        public void ReducingCameraMovementDoesNotRescaleOrResetTravelledDistance()
        {
            var storm = new StormResponse();
            storm.Advance(5, new(.2, .1, .1, .1, 0), 0);
            var distance = storm.Distance;
            storm.CameraMovement = 0;
            storm.Advance(0, default, 0);
            Assert.That(storm.Distance, Is.EqualTo(distance));
            storm.Advance(5, default, 0);
            Assert.That(storm.MovementScale, Is.LessThan(.001));
            Assert.That(storm.Distance, Is.GreaterThanOrEqualTo(distance));
        }

        static AudioFeatures Tone(double frequency)
        {
            const int rate = 48000;
            var samples = new double[rate];
            for (var i = 0; i < samples.Length; ++i)
                samples[i] = .4 * Math.Sin(2 * Math.PI * frequency * i / rate);
            var extractor = new AudioFeatureExtractor(new(SampleType.Float32.Instance, rate, 1));
            return extractor.Process(new(samples, samples.Length, DateTime.Now));
        }

        [Test]
        public void RawFeaturesMeasureLevelAndDistinguishLowAndHighFrequencyEnergy()
        {
            var bass = Tone(50);
            var air = Tone(8000);
            Assert.That(bass.Level, Is.EqualTo(.4 / Math.Sqrt(2)).Within(1e-10));
            Assert.That(air.Level, Is.EqualTo(bass.Level).Within(1e-10));
            Assert.That(bass.Bass, Is.GreaterThan(bass.Air * 5));
            Assert.That(air.Air, Is.GreaterThan(air.Bass * 5));
            Assert.That(bass.Stereo, Is.Zero);
        }

        [Test]
        public void FeatureExtractionIsIndependentOfSampleChunking()
        {
            var samples = new double[997];
            for (var i = 0; i < samples.Length; ++i)
                samples[i] = .4 * Math.Sin(i * .07) + .2 * Math.Cos(i * .73);
            var format = new SampleFormat(SampleType.Float32.Instance, 48000, 1);
            var expected = new AudioFeatureExtractor(format).Process(new(samples, samples.Length, DateTime.Now));
            var chunks = new AudioFeatureExtractor(format);
            double level = 0, bass = 0, body = 0, air = 0;
            for (var offset = 0; offset < samples.Length; offset += 73)
            {
                var count = Math.Min(73, samples.Length - offset);
                var chunk = samples.AsSpan(offset, count).ToArray();
                var result = chunks.Process(new(chunk, count, DateTime.Now));
                level += result.Level * result.Level * count;
                bass += result.Bass * result.Bass * count;
                body += result.Body * result.Body * count;
                air += result.Air * result.Air * count;
            }
            Assert.That(Math.Sqrt(level / samples.Length), Is.EqualTo(expected.Level).Within(1e-12));
            Assert.That(Math.Sqrt(bass / samples.Length), Is.EqualTo(expected.Bass).Within(1e-12));
            Assert.That(Math.Sqrt(body / samples.Length), Is.EqualTo(expected.Body).Within(1e-12));
            Assert.That(Math.Sqrt(air / samples.Length), Is.EqualTo(expected.Air).Within(1e-12));
        }
    }
}

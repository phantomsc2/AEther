using System;
using NUnit.Framework;

namespace AEther.Tests
{
    public class ResponseTests
    {
        [Test]
        public void OneTransientCanHaveImmediateLightAndSlowMotion()
        {
            var light = new ResponseChannel(new(0, .09, 0));
            var motion = new ResponseChannel(new(0, 1.5, 2.8));
            light.Advance(0, 1);
            motion.Advance(0, 1);
            Assert.That(light.Envelope, Is.EqualTo(1));
            Assert.That(motion.Envelope, Is.EqualTo(1));
            Assert.That(motion.Inertial, Is.Zero);
            for (var i = 0; i < 30; ++i)
            {
                light.Advance(1.0 / 60, 0);
                motion.Advance(1.0 / 60, 0);
            }
            Assert.That(light.Envelope, Is.LessThan(.01));
            Assert.That(motion.Inertial, Is.GreaterThan(0));
            for (var i = 0; i < 1800; ++i) motion.Advance(1.0 / 60, 0);
            Assert.That(Math.Abs(motion.Inertial), Is.LessThan(1e-6));
        }

        [Test]
        public void AttackParameterControlsResponseWithoutChangingTheSource()
        {
            var immediate = new ResponseChannel(new(0, .4, 0));
            var delayed = new ResponseChannel(new(.5, .4, 0));
            immediate.Advance(.02, .7);
            delayed.Advance(.02, .7);
            Assert.That(immediate.Envelope, Is.EqualTo(.7));
            Assert.That(delayed.Envelope, Is.LessThan(.05));
        }

        [Test]
        public void ConstantTargetInertiaIsIndependentOfFrameRate()
        {
            var coarse = new ResponseChannel(new(0, 1, 2.8));
            var fine = new ResponseChannel(new(0, 1, 2.8));
            coarse.Advance(.5, .8);
            for (var i = 0; i < 120; ++i) fine.Advance(.5 / 120, .8);
            Assert.That(fine.Inertial, Is.EqualTo(coarse.Inertial).Within(1e-12));
            Assert.That(fine.Velocity, Is.EqualTo(coarse.Velocity).Within(1e-12));
        }

        [TestCase(.35, 5, 2.8)]
        [TestCase(.5, .5, 1)]
        public void ChangingEnvelopeAndInertiaIntegrateIndependentlyOfFrameRate(double attack, double release, double inertia)
        {
            var coarse = new ResponseChannel(new(attack, release, inertia));
            var fine = new ResponseChannel(new(attack, release, inertia));
            foreach (var target in new[] { 1.0, .2, 0.0 })
            {
                coarse.Advance(.5, target);
                for (var i = 0; i < 120; ++i) fine.Advance(.5 / 120, target);
                Assert.That(fine.Envelope, Is.EqualTo(coarse.Envelope).Within(1e-11));
                Assert.That(fine.Inertial, Is.EqualTo(coarse.Inertial).Within(1e-11));
                Assert.That(fine.Velocity, Is.EqualTo(coarse.Velocity).Within(1e-11));
            }
        }

        [Test]
        public void LowSustainedAndHighTransientControlsUseDifferentFrequencyRegions()
        {
            var domain = new Domain(27.5, 8, 65);
            var extractor = new SpectralControlExtractor(domain, 1);
            var data = new double[4 * domain.Length];
            var input = new SampleEvent<double>(data, data.Length, DateTime.Now);
            data[4 * 6] = 1;
            data[4 * 6 + 3] = .8;
            var lowTone = extractor.Process(input);
            data[4 * 6] = 0;
            data[4 * 6 + 1] = 1;
            var lowAttack = extractor.Process(input);
            data[4 * 6 + 1] = 0;
            data[4 * 62 + 1] = 1;
            data[4 * 62 + 3] = .8;
            var highAttack = extractor.Process(input);
            Assert.That(lowTone.LowSustain, Is.GreaterThan(.1));
            Assert.That(lowTone.HighTransient, Is.Zero);
            Assert.That(highAttack.HighTransient, Is.GreaterThan(.9));
            Assert.That(lowAttack.HighTransient, Is.LessThan(.001));
        }

        [Test]
        public void ContrastsBelowTheAudibleFloorDoNotFlashTheScene()
        {
            var domain = new Domain(27.5, 8, 65);
            var data = new double[4 * domain.Length];
            for (var i = 0; i < domain.Length; ++i)
            {
                data[4 * i] = 10;
                data[4 * i + 1] = 10;
                data[4 * i + 3] = .1;
            }
            Assert.That(new SpectralControlExtractor(domain, 1).Process(new(data, data.Length, DateTime.Now)),
                Is.EqualTo(default(SpectralControls)));
        }

        [Test]
        public void SeparatedHighAttacksFlashWhileSustainedLowEnergyBuildsOrbitalMomentum()
        {
            var storm = new StormResponse();
            var features = new AudioFeatures(.2, .1, .1, .05, 0);
            for (var i = 0; i < 300; ++i)
                storm.Advance(1.0 / 60, features, 0, new(.5, 0));
            Assert.That(storm.Flash, Is.Zero);
            Assert.That(storm.OrbitResponse, Is.GreaterThan(.4));
            var orbit = storm.Yaw;
            storm.Advance(0, features, 0, new(.5, .3));
            Assert.That(storm.Flash, Is.EqualTo(1));
            Assert.That(storm.Yaw, Is.EqualTo(orbit));
        }

        [Test]
        public void StationaryCameraDoesNotFollowCloudTimeOrChangingMusicalEnergy()
        {
            var storm = new StormResponse();
            storm.CameraMovement = 0;
            for (var i = 0; i < 1800; ++i) storm.Advance(1.0 / 60, default, 0);
            var pose = (storm.Yaw, storm.Pitch, storm.Roll);
            for (var i = 0; i < 300; ++i) storm.Advance(1.0 / 60, new(.3, .2, .1, .1, 0), .2);
            Assert.That(storm.Yaw, Is.EqualTo(pose.Yaw).Within(1e-12));
            Assert.That(storm.Pitch, Is.EqualTo(pose.Pitch).Within(1e-12));
            Assert.That(storm.Roll, Is.EqualTo(pose.Roll).Within(1e-12));
        }
    }
}

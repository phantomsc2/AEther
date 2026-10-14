using System;
using NUnit.Framework;

namespace AEther.Tests
{
    public class ImpulseTests
    {
        [Test]
        public void AnAttackIsImmediateAndSilenceDoesNotRetrigger()
        {
            var detector = new TransientDetector(48000);
            Assert.That(detector.Process(new double[] { 0, 0, 1 }), Is.EqualTo(1));
            Assert.That(detector.Process(new double[4800]), Is.Zero);
            Assert.That(detector.Process(new double[] { double.NaN, double.PositiveInfinity }), Is.Zero);
        }

        [Test]
        public void ImpulseChangesVelocityWithoutTeleportingAndThenRelaxes()
        {
            var motion = new ImpulseMotion();
            motion.AddImpulse(1);
            Assert.That(motion.Position, Is.Zero);
            Assert.That(motion.Velocity, Is.EqualTo(1));
            motion.Advance(.1);
            Assert.That(motion.Position, Is.GreaterThan(0));
            motion.Advance(5);
            Assert.That(Math.Abs(motion.Position), Is.LessThan(1e-8));
            Assert.That(Math.Abs(motion.Velocity), Is.LessThan(1e-8));
        }

        [Test]
        public void MotionIsIndependentOfFrameSubdivision()
        {
            var coarse = new ImpulseMotion();
            var fine = new ImpulseMotion();
            coarse.AddImpulse(1);
            fine.AddImpulse(1);
            coarse.Advance(.5);
            for (var i = 0; i < 60; ++i)
                fine.Advance(.5 / 60);
            Assert.That(fine.Position, Is.EqualTo(coarse.Position).Within(1e-12));
            Assert.That(fine.Velocity, Is.EqualTo(coarse.Velocity).Within(1e-12));
        }
    }
}

using System;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests
{
    public class SplitterDesignTests
    {
        [Test]
        public void VisualFeaturesReadLinearMagnitudeAndIncludeAllThreeComponents()
        {
            var extractor = new RetainedVisualFeatures(new Domain(100, 8, 1), 1, 100);
            var features = extractor.Process(new(new[] { .1, .2, .2, .5 }, 4, DateTime.Now));
            Assert.That(features.Level, Is.EqualTo(.5 / Math.Sqrt(2)).Within(1e-12));
            Assert.That(features.Bass, Is.EqualTo(features.Level));
            Assert.That(features.MusicalTime, Is.EqualTo(.25 / 200).Within(1e-12));
            Assert.That(extractor.Transient, Is.EqualTo(.2));
        }

        [Test]
        public void TextureRowsPreserveAdditiveMagnitudesAboveFullScale()
        {
            var history = new AudioHistory(1, 100);
            history.Post(new(new[] { .5, 1.5, 2.0, 4.0 }, 4, DateTime.Now), 1);
            history.Advance(0);
            Assert.That(history.History[0].AsSpan(0, 4).ToArray(), Is.EqualTo(new[] { .5f, 1.5f, 2f, 4f }));
            Assert.That(history.Energy, Is.EqualTo(4));
            Assert.That(history.MusicalTime, Is.EqualTo(.16).Within(1e-12));
        }
    }
}

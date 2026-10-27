using System;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests
{
    public class SplitterDesignTests
    {
        [Test]
        public void TextureRowsPreserveGainedComponentsIndependentlyOfMagnitude()
        {
            var history = new AudioHistory(1, 100);
            history.Post(new(new[] { .5, 1.5, 2.0, .4 }, 4, DateTime.Now), 1);
            history.Advance(0);
            Assert.That(history.History[0].AsSpan(0, 4).ToArray(), Is.EqualTo(new[] { .5f, 1.5f, 2f, .4f }));
            Assert.That(history.Energy, Is.EqualTo(.4f));
        }
    }
}

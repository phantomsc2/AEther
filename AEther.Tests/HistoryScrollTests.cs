using NUnit.Framework;

namespace AEther.Tests
{
    public class HistoryScrollTests
    {
        [Test]
        public void BurstyCapturePacketsDoNotStepThePlayhead()
        {
            var scroll = new HistoryScroll(256, .04);
            for (var i = 0; i < 16; ++i)
                scroll.AddRow();
            scroll.Advance(0);
            var previous = scroll.Position;
            // Four analysis rows arrive as one packet every other 120 Hz render frame.
            for (var frame = 0; frame < 100; ++frame)
            {
                if (frame % 2 == 0)
                    for (var i = 0; i < 4; ++i)
                        scroll.AddRow();
                scroll.Advance(1.0 / 128);
                Assert.That(scroll.Position - previous, Is.EqualTo(2).Within(1e-10));
                previous = scroll.Position;
            }
        }

        [Test]
        public void NeverRunsAheadOfRecordedAudioOrMovesBackwardAfterAnUnderrun()
        {
            var scroll = new HistoryScroll(256, 0);
            scroll.AddRow();
            scroll.Advance(0);
            scroll.Advance(1);
            Assert.That(scroll.Position, Is.Zero);
            scroll.AddRow();
            scroll.Advance(.001);
            Assert.That(scroll.Position, Is.EqualTo(.256).Within(1e-12));
        }

        [Test]
        public void RingCoordinatesReferToRowCentersAndWrapContinuously()
        {
            var scroll = new HistoryScroll(1, 0);
            for (var i = 0; i < 4; ++i)
                scroll.AddRow();
            scroll.Advance(0);
            Assert.That(scroll.TextureCoordinate(4), Is.EqualTo(.875));
            scroll.AddRow();
            scroll.Advance(.5);
            Assert.That(scroll.TextureCoordinate(4), Is.EqualTo(0));
            scroll.Advance(.5);
            Assert.That(scroll.TextureCoordinate(4), Is.EqualTo(.125));
        }

        [Test]
        public void DelayedPlayheadExcludesRowsOverwrittenBeyondItsOldestValidTime()
        {
            var scroll = new HistoryScroll(100, .04);
            for (var i = 0; i < 10; ++i)
                scroll.AddRow();
            scroll.Advance(0);
            Assert.That(scroll.AvailableSpan(100), Is.EqualTo(.96).Within(1e-12));
        }
    }
}

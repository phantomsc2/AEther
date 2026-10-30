using System;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

public class AdaptiveScatterRasterTests
{
    [TestCase(0, 640, 320)]
    [TestCase(100, 150, 100)]
    [TestCase(100, 250, 100)]
    [TestCase(100, 149, 75)]
    [TestCase(100, 251, 126)]
    [TestCase(0, 1, 2)]
    [TestCase(100, 0, 100)]
    [TestCase(100, double.NaN, 100)]
    public void DimensionsRespectHysteresis(int current, double physical, int expected)
        => Assert.That(AdaptiveScatterRaster.Dimension(current, physical), Is.EqualTo(expected));

    [Test]
    public void DpiAndAxesAreIndependent()
    {
        Assert.That(AdaptiveScatterRaster.Dimension(320, 640 * 1.5), Is.EqualTo(480));
        Assert.That(AdaptiveScatterRaster.Dimension(128, 256), Is.EqualTo(128));
    }

    [Test]
    public void RestorationCopiesNearestMarks()
    {
        var source = new[] { 0, 42, 0, 0 };
        var restored = new int[9];
        AdaptiveScatterRaster.Restore(source, 2, 2, restored, 3, 3);
        Assert.That(restored, Is.EqualTo(new[] { 0, 0, 42, 0, 0, 42, 0, 0, 0 }));
    }

    [TestCase(0, 1, 0, 1)]
    [TestCase(6, 8, 6, 1)]
    [TestCase(2, 10, 0, 7)]
    public void SweepClearsColumnsAfterResize(double previous, double now, int first, int last)
    {
        var restored = new int[16];
        AdaptiveScatterRaster.Restore([42, 42, 42, 42], 2, 2, restored, 8, 2);
        Assert.That(AdaptiveScatterRaster.ClearSweep(restored, 8, 2, previous, now, 7), Is.True);
        for (int x = 0; x < 8; x++)
        {
            bool cleared = first <= last ? x >= first && x <= last : x >= first || x <= last;
            Assert.That(restored[x], Is.EqualTo(cleared ? 0 : 42));
            Assert.That(restored[8 + x], Is.EqualTo(cleared ? 0 : 42));
        }
    }
}

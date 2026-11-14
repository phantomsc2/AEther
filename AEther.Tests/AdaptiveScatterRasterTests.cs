using System;
using System.Linq;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

public class AdaptiveScatterRasterTests
{
    [TestCase(640, 320)]
    [TestCase(150, 75)]
    [TestCase(250, 125)]
    [TestCase(1, 2)]
    [TestCase(0, 0)]
    [TestCase(double.NaN, 0)]
    public void DimensionsTrackHalfPhysicalSize(double physical, int expected)
        => Assert.That(AdaptiveScatterRaster.Dimension(physical), Is.EqualTo(expected));

    [TestCase(0, 1, 0, 1)]
    [TestCase(6, 8, 6, 1)]
    [TestCase(2, 10, 0, 7)]
    public void SweepClearsColumnsAfterResize(double previous, double now, int first, int last)
    {
        var restored = Enumerable.Repeat(42, 16).ToArray();
        Assert.That(AdaptiveScatterRaster.ClearSweep(restored, 8, 2, previous, now, 7), Is.True);
        for (int x = 0; x < 8; x++)
        {
            bool cleared = first <= last ? x >= first && x <= last : x >= first || x <= last;
            Assert.That(restored[x], Is.EqualTo(cleared ? 0 : 42));
            Assert.That(restored[8 + x], Is.EqualTo(cleared ? 0 : 42));
        }
    }
}

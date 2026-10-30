using System.Collections.Generic;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture]
public class ScatterRasterTests
{
    [Test]
    public void EveryNativeSampleIsDrawnIncludingSingleSampleSpikes()
    {
        var points = new List<ScatterPoint>();
        for (int x = 0; x < 700; x++) points.Add(new(x / 100.0, -1));
        points.Add(new(3.141, 2));
        var pixels = new int[701 * 101];
        ScatterRaster.Draw(pixels, 701, 101, points, unchecked((int)0xffff0000), -2, 2, 7);
        Assert.That(pixels[314], Is.EqualTo(unchecked((int)0xffff0000)), "One high sample must survive among thousands of low samples.");
        Assert.That(pixels[75 * 701], Is.EqualTo(unchecked((int)0xffff0000)));
        Assert.That(pixels[75 * 701 + 699], Is.EqualTo(unchecked((int)0xffff0000)));
    }

    [Test]
    public void TimelineWrapsAndDoesNotConnectAcrossTheSeam()
    {
        var pixels = new int[8 * 5];
        ScatterRaster.Draw(pixels, 8, 5, [new(0, 0), new(7, 0), new(-.5, 0), new(14, 0)], unchecked((int)0xff00ff00), -1, 1, 7);
        Assert.That(pixels[2 * 8], Is.EqualTo(unchecked((int)0xff00ff00)));
        Assert.That(pixels[2 * 8 + 6], Is.EqualTo(unchecked((int)0xff00ff00)));
        Assert.That(pixels[2 * 8 + 3], Is.Zero);
    }

    [Test]
    public void OutOfRangeAndNonfiniteValuesCannotWriteOutsideTheImage()
    {
        var pixels = new int[8 * 5];
        ScatterRaster.Draw(pixels, 8, 5,
            [new(1, 100), new(1, -100), new(double.NaN, 0), new(1, double.PositiveInfinity)],
            unchecked((int)0xff00ff00), -1, 1, 7);
        Assert.That(pixels, Is.All.Zero);
    }
}

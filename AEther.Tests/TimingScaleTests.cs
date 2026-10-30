using System;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture]
public class TimingScaleTests
{
    static double L(double value) => Math.Log10(value);

    [Test]
    public void BoundsUseOneTwoFiveAndExpandImmediately()
    {
        var scale = new TimingScale();
        Assert.That(scale.Update(L(.07), L(1.3), 0), Is.True);
        Assert.That(scale.Lower, Is.EqualTo(L(.05)).Within(1e-12));
        Assert.That(scale.Upper, Is.EqualTo(L(2)).Within(1e-12));
        Assert.That(scale.Update(L(.07), L(11), .1), Is.True);
        Assert.That(scale.Upper, Is.EqualTo(L(20)).Within(1e-12));
        Assert.That(scale.Update(L(.004), L(11), .2), Is.True);
        Assert.That(scale.Lower, Is.EqualTo(L(.002)).Within(1e-12));
    }

    [Test]
    public void ShrinkWaitsAndResetsWhenDataNoLongerFits()
    {
        var scale = new TimingScale();
        scale.Update(L(.01), L(100), 0);
        scale.Update(L(.1), L(1.3), 7);
        Assert.That(scale.Update(L(.1), L(1.3), 9.9), Is.False);
        scale.Update(L(.1), L(6), 9.95); // invalidates the pending 2 ms bound
        scale.Update(L(.1), L(1.3), 10);
        Assert.That(scale.Upper, Is.EqualTo(L(100)).Within(1e-12));
        Assert.That(scale.Update(L(.1), L(1.3), 12.96), Is.True);
        Assert.That(scale.Upper, Is.EqualTo(L(10)).Within(1e-12));
        scale.Update(L(.1), L(1.3), 13);
        scale.Update(L(.1), L(1.3), 16);
        Assert.That(scale.Upper, Is.EqualTo(L(2)).Within(1e-12));
    }

    [Test]
    public void EmptyInputAndSingleExactSampleAreSafe()
    {
        var scale = new TimingScale();
        Assert.That(scale.Update(double.PositiveInfinity, double.NegativeInfinity, 0), Is.False);
        Assert.That(scale.Lower, Is.Null);
        scale.Update(0, 0, 1);
        Assert.That(scale.Lower, Is.EqualTo(0));
        Assert.That(scale.Upper, Is.GreaterThan(0));
        var ticks = TimingScale.Ticks(scale.Lower!.Value, scale.Upper!.Value);
        Assert.That(ticks, Has.Length.EqualTo(2));
        scale.Reset();
        Assert.That(scale.Upper, Is.Null);
    }

    [Test]
    public void CacheResetReusesBufferAndClearsOldPoints()
    {
        var cache = new ScatterCache(8, 8, -2, 2, 7, [unchecked((int)0xffff0000)], []);
        var pixels = cache.Pixels;
        cache.Add(0, new(1, 0));
        cache.Reset(-1, 1, []);
        Assert.That(cache.Pixels, Is.SameAs(pixels));
        Assert.That(cache.Pixels, Is.All.Zero);
        cache.Add(0, new(1, 0));
        cache.Remove(0, new(1, 0));
        Assert.That(cache.Pixels, Is.All.Zero);
    }
}

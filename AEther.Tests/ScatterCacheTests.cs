using System.Collections.Generic;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture]
public class ScatterCacheTests
{
    [Test]
    public void VisibilityRecomposesOverlapsAndTracksHiddenChanges()
    {
        var cache = new ScatterCache(5, 5, -2, 2, 4,
            [unchecked((int)0xffff0000), unchecked((int)0xff00ff00)], [0]);
        var center = new ScatterPoint(2, 0);
        var left = new ScatterPoint(1, 0);
        cache.Add(0, center);
        cache.Add(1, left);
        cache.Add(1, center);
        cache.SetVisible(1, false);
        Assert.That(cache.Pixels[12], Is.EqualTo(unchecked((int)0xffff0000)));
        cache.SetVisible(0, false);
        Assert.That(cache.Pixels[12], Is.EqualTo(0x30303030));
        cache.Remove(1, center);
        cache.Add(1, left);
        cache.SetVisible(1, true);
        Assert.That(cache.Pixels[12], Is.EqualTo(0x64246424));
        cache.Remove(1, left);
        cache.Remove(1, left);
        Assert.That(cache.Pixels[12], Is.EqualTo(0x30303030));
        cache.SetVisible(0, true);
        Assert.That(cache.Pixels[12], Is.EqualTo(unchecked((int)0xffff0000)));
    }

    [Test]
    public void VisibilitySurvivesResetAndMatchesFreshCache()
    {
        var colors = new[] { unchecked((int)0xffff0000), unchecked((int)0xff00ff00) };
        var cache = new ScatterCache(10, 10, -2, 2, 4, colors, []);
        cache.SetVisible(0, false);
        cache.Reset(-1, 1, [0]);
        var point = new ScatterPoint(2, 0);
        cache.Add(0, point);
        cache.Add(1, point);
        var reference = new ScatterCache(10, 10, -1, 1, 4, colors, [0]);
        reference.Add(1, point);
        Assert.That(cache.Pixels, Is.EqualTo(reference.Pixels));
        cache.SetVisible(1, false);
        cache.SetVisible(0, true);
        var other = new ScatterCache(10, 10, -1, 1, 4, colors, [0]);
        other.Add(0, point);
        Assert.That(cache.Pixels, Is.EqualTo(other.Pixels));
    }

    [Test]
    public void CrossHasOpaqueCenterQuarterOpacityNeighborsAndNoDiagonals()
    {
        var cache = new ScatterCache(5, 5, -2, 2, 4, [unchecked((int)0xffff0000)], []);
        cache.Add(0, new(2, 0));
        Assert.That(cache.Pixels[12], Is.EqualTo(unchecked((int)0xffff0000)));
        foreach (int i in new[] { 7, 11, 13, 17 })
            Assert.That(cache.Pixels[i], Is.EqualTo(0x40400000));
        Assert.That(System.Linq.Enumerable.Count(cache.Pixels, p => p != 0), Is.EqualTo(5));
    }

    [Test]
    public void KernelClipsAtCornerWithoutWrapping()
    {
        var cache = new ScatterCache(5, 5, -2, 2, 4, [unchecked((int)0xffff0000)], []);
        cache.Add(0, new(0, 2));
        Assert.That(System.Linq.Enumerable.Count(cache.Pixels, p => p != 0), Is.EqualTo(3));
        Assert.That(cache.Pixels[4], Is.Zero);
        cache.Remove(0, new(0, 2));
        Assert.That(cache.Pixels, Is.All.Zero);
    }

    [Test]
    public void ExpiringCenterRevealsNonAccumulatingHaloAndRestoresGrid()
    {
        var cache = new ScatterCache(5, 5, -2, 2, 4,
            [unchecked((int)0xffff0000), unchecked((int)0xff00ff00)], [0]);
        var left = new ScatterPoint(1, 0);
        var center = new ScatterPoint(2, 0);
        cache.Add(1, left); cache.Add(1, left);
        cache.Add(0, center);
        // An earlier trace center wins over later trace halos.
        Assert.That(cache.Pixels[12], Is.EqualTo(unchecked((int)0xffff0000)));
        cache.Remove(0, center);
        Assert.That(cache.Pixels[12], Is.EqualTo(0x64246424));
        cache.Remove(1, left);
        Assert.That(cache.Pixels[12], Is.EqualTo(0x64246424));
        cache.Remove(1, left);
        Assert.That(cache.Pixels[12], Is.EqualTo(0x30303030));
    }
    [Test]
    public void ExpirationPreservesOverlappingSamplesAndRevealsEarlierTraces()
    {
        var cache = new ScatterCache(100, 40, -2, 2, 7, [unchecked((int)0xffff0000), unchecked((int)0xff00ff00)], []);
        var p = new ScatterPoint(1, 0);
        cache.Add(0, p); cache.Add(0, p); cache.Add(1, p);
        cache.Remove(1, p);
        Assert.That(cache.Pixels, Does.Contain(unchecked((int)0xffff0000)));
        cache.Remove(0, p);
        Assert.That(cache.Pixels, Does.Contain(unchecked((int)0xffff0000)));
        cache.Remove(0, p);
        Assert.That(cache.Pixels, Is.All.Zero);
    }

    [Test]
    public void IncrementalInsertionAndExpirationMatchFullRedrawAcrossWrap()
    {
        var cache = new ScatterCache(701, 101, -2, 2, 7, [unchecked((int)0xffff0000), unchecked((int)0xff00ff00)], []);
        var a = new List<ScatterPoint>();
        var b = new List<ScatterPoint>();
        for (int i = 0; i < 1800; i++)
        {
            var point = new ScatterPoint(i * .01, i % 7 == 0 ? 2 : -1);
            a.Add(point); cache.Add(0, point);
            if (i % 3 == 0) { b.Add(point); cache.Add(1, point); }
            while (a.Count > 0 && a[0].Time < point.Time - 7)
            { cache.Remove(0, a[0]); a.RemoveAt(0); }
            while (b.Count > 0 && b[0].Time < point.Time - 7)
            { cache.Remove(1, b[0]); b.RemoveAt(0); }
        }
        var reference = new int[701 * 101];
        ScatterRaster.Draw(reference, 701, 101, a, unchecked((int)0xffff0000), -2, 2, 7);
        ScatterRaster.Draw(reference, 701, 101, b, unchecked((int)0xff00ff00), -2, 2, 7);
        Assert.That(cache.Pixels, Is.EqualTo(reference));
    }
}

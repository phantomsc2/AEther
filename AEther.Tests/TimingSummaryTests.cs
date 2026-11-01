using System;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture]
public class TimingSummaryTests
{
    [Test]
    public void ComputesPopulationStatisticsIncludingZero()
    {
        var summary = new TimingSummary();
        foreach (double value in new[] { 0.0, 2, 4, 6 }) summary.Add(value);
        Assert.That(summary.Count, Is.EqualTo(4));
        Assert.That(summary.Mean, Is.EqualTo(3));
        Assert.That(summary.StandardDeviation, Is.EqualTo(Math.Sqrt(5)).Within(1e-12));
        Assert.That(summary.Max, Is.EqualTo(6));
    }

    [Test]
    public void IgnoresInvalidDurations()
    {
        var summary = new TimingSummary();
        foreach (double value in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            summary.Add(value);
        Assert.That(summary.Count, Is.Zero);
        summary.Add(7);
        Assert.That(summary.Count, Is.EqualTo(1));
        Assert.That(summary.Mean, Is.EqualTo(7));
        Assert.That(summary.Max, Is.EqualTo(7));
        Assert.That(summary.StandardDeviation, Is.Zero);
    }

    [Test]
    public void ResetStartsAnIndependentWindow()
    {
        var summary = new TimingSummary();
        summary.Add(100);
        summary.Add(200);
        summary.Reset();
        Assert.That(summary.Count, Is.Zero);
        Assert.That(summary.Mean, Is.Zero);
        Assert.That(summary.Max, Is.Zero);
        Assert.That(summary.StandardDeviation, Is.Zero);
        summary.Add(2);
        summary.Add(4);
        Assert.That(summary.Mean, Is.EqualTo(3));
        Assert.That(summary.Max, Is.EqualTo(4));
        Assert.That(summary.StandardDeviation, Is.EqualTo(1));
    }

    [Test]
    public void PreservesSmallVarianceAtLargeMagnitudes()
    {
        var summary = new TimingSummary();
        summary.Add(1e12 - 1);
        summary.Add(1e12);
        summary.Add(1e12 + 1);
        Assert.That(summary.Mean, Is.EqualTo(1e12));
        Assert.That(summary.StandardDeviation, Is.EqualTo(Math.Sqrt(2.0 / 3)).Within(1e-12));
    }
}

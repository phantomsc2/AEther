using System;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class AudioHistoryTests
{
    [Test]
    public void DelayedHistoryReservesCapacityForTheFullVisibleWindow()
    {
        var history = new AudioHistory(1, 256, 1, .02);
        Assert.That(history.Rows, Is.GreaterThanOrEqualTo(Math.Ceiling(1.02 * 256) + 2));
        history.Post(new(new[] { .2, .3, .4, .5 }, 4, DateTime.Now) { AnalysisRow = 1000 }, 1);
        history.Advance(0);
        Assert.That(history.AvailableSpan, Is.GreaterThanOrEqualTo(history.TimeScale));
        Assert.That(history.AvailableSpan, Is.LessThan(1));
    }

    [Test]
    public void StartupDoesNotExposeWrappedFutureRowsAsOldHistory()
    {
        var history = new AudioHistory(1, 256);
        history.Post(new(new[] { .2, .3, .4, .5 }, 4, DateTime.Now) { AnalysisRow = 0 }, 1);
        history.Advance(0);
        Assert.That(history.AvailableSpan, Is.Zero);
    }

    [Test]
    public void FixedRowsIgnoreWallTimeAndPreserveSkippedRows()
    {
        var history = new AudioHistory(1, 100);
        history.Post(new(new[] { .2, .3, .4, .5 }, 4, DateTime.Now) { AnalysisRow = 0 }, 1);
        history.Post(new(new[] { .6, .7, .8, .9 }, 4, DateTime.Now.AddHours(-1)) { AnalysisRow = 2 }, 1);
        history.Advance(0);
        Assert.That(history.History[0][0], Is.EqualTo(.2f));
        Assert.That(history.History[0].AsSpan(4, 4).ToArray(), Is.All.Zero);
        Assert.That(history.History[0][8], Is.EqualTo(.6f));
    }

    [Test]
    public void SpectrumTakesComponentMaximaOnlySincePreviousFrame()
    {
        var history = new AudioHistory(1, 100);
        history.Post(new(new[] { .8, .1, .6, .2 }, 4, DateTime.Now), 1);
        history.Post(new(new[] { .2, .9, .3, .7 }, 4, DateTime.Now) { AnalysisRow = 1 }, 1);
        history.Advance(0);
        Assert.That(history.Spectrum[0], Is.EqualTo(new[] { .8f, .9f, .6f, .7f }));
        history.Post(new(new[] { .1, .2, .3, .4 }, 4, DateTime.Now) { AnalysisRow = 2 }, 1);
        history.Advance(0);
        Assert.That(history.Spectrum[0], Is.EqualTo(new[] { .1f, .2f, .3f, .4f }));
        history.Advance(0);
        Assert.That(history.Spectrum[0], Is.All.Zero);
    }

    [Test]
    public void QueuedAudioOwnsItsStorageAndMonoIsMirrored()
    {
        var history = new AudioHistory(4, 100);
        var samples = new double[16];
        Array.Fill(samples, .5);
        history.Post(new(samples, 16, DateTime.Now - TimeSpan.FromSeconds(.01)), 1);
        Array.Fill(samples, 0);
        history.Advance(0);
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.5f));
        Assert.That(history.Spectrum[1], Is.EqualTo(history.Spectrum[0]));
    }

    [Test]
    public void BacklogIsBoundedAndNewestPacketWins()
    {
        var history = new AudioHistory(4, 100);
        var samples = new double[16];
        for (var row = 0; row < 1000; row++)
        {
            Array.Fill(samples, row == 999 ? .75 : .1);
            history.Post(new(samples, 16, DateTime.Now - TimeSpan.FromSeconds(.01)), 1);
        }
        history.Advance(0);
        Assert.That(history.DroppedRows, Is.GreaterThan(0));
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.75f));
    }

    [Test]
    public void InvalidValuesDoNotReachTextures()
    {
        var history = new AudioHistory(1, 100);
        history.Post(new(new[] { double.NaN, double.PositiveInfinity, -1.0, 2.0 }, 4,
            DateTime.Now - TimeSpan.FromSeconds(.01)), 1);
        history.Advance(0);
        Assert.That(history.Spectrum[0], Is.EqualTo(new[] { 0f, 0f, 0f, 2f }));
    }
}

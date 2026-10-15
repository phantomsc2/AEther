using System;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class AudioHistoryTests
{
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
        Assert.That(history.Spectrum[0], Is.EqualTo(new[] { 0f, 0f, 0f, 1f }));
    }
}

using System;
using System.Linq;
using System.Numerics;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class GlobalHistoryTests
{
    [Test]
    public void GlobalAndLocalFieldsShareTheFrameIntervalAndRetainSignedHistory()
    {
        var clock = new Clock();
        var history = new AudioHistory(1, 100, timeProvider: clock);
        var samples = new[] { .8, .1, .6, .2 };
        var first = new GlobalEvidence(.5, .7, .3, .2, 1, 3);
        var last = new GlobalEvidence(.2, -.1, .1, .05, 1, 4);
        history.Post(new(samples, 4, DateTime.UnixEpoch) { AnalysisRow = 0, GlobalEvidence = first }, 1);
        history.Post(new(new[] { .2, .9, .3, .7 }, 4, DateTime.UnixEpoch)
            { AnalysisRow = 2, GlobalEvidence = last }, 1);
        Array.Clear(samples);
        history.Advance(0);
        var aggregate = history.GlobalEvidence;
        Assert.That(aggregate.Background, Is.EqualTo((.5 + .2) / 3).Within(1e-12));
        Assert.That(aggregate.Transient, Is.EqualTo((.7 - .1) / 3).Within(1e-12));
        Assert.That(Vector4.Distance(history.GlobalSTBR, new Vector4(.4f / 3, .6f / 3, .7f / 3, .25f / 3)), Is.LessThan(1e-7));
        Assert.That(history.Spectrum[0], Is.EqualTo(new[] { 1f / 3, 1f / 3, .9f / 3, .9f / 3 }).Within(1e-7));
        Assert.That(history.GlobalHistory.Take(4), Is.EqualTo(new[] { .3f, .7f, .5f, .2f }));
        Assert.That(history.GlobalHistory.Skip(4).Take(4), Is.All.Zero);
        Assert.That(history.GlobalHistory.Skip(8).Take(4), Is.EqualTo(new[] { .1f, -.1f, .2f, .05f }));
        history.Advance(0);
        Assert.That(history.GlobalEvidence, Is.EqualTo(aggregate), "Frame aggregates survive frames without new rows.");
        clock.Milliseconds = 200;
        history.Advance(.2);
        Assert.That(history.GlobalSTBR, Is.EqualTo(Vector4.Zero));
        Assert.That(history.GlobalHistory[9], Is.EqualTo(-.1f), "Expiry masks history using the same age as local data.");
        // Resume without consumed sample rows: both histories must clear the pause.
        history.Post(new(new double[4], 4, DateTime.UnixEpoch) { AnalysisRow = 3 }, 1);
        history.Advance(0);
        Assert.That(history.GlobalHistory[2], Is.EqualTo(.5f), "A pause retains correctly aged history.");
        Assert.That(history.GlobalSTBR, Is.EqualTo(Vector4.Zero));
    }

    [Test]
    public void RingWrapAndLargeGapsClearExactlyTheSameRows()
    {
        var clock = new Clock();
        var history = new AudioHistory(1, 100, timeProvider: clock);
        for (int row = 0; row < 2 * history.Rows; row++) Post(row);
        Post(2 * history.Rows + 3);
        for (int slot = 0; slot < history.Rows; slot++)
            Assert.That(history.GlobalHistory[4 * slot + 2], Is.EqualTo(history.History[0][4 * slot + 2]));
        Post(5 * history.Rows);
        Assert.That(history.GlobalHistory.Count(x => x != 0), Is.EqualTo(1));
        Assert.That(history.GlobalSTBR.Z, Is.LessThan(5 * history.Rows), "The gap contributes zero to the frame mean.");

        void Post(int row)
        {
            clock.Milliseconds++;
            history.Post(new(new[] { 0.0, 0, (double)row, 0 }, 4, DateTime.UnixEpoch)
                { AnalysisRow = row, GlobalEvidence = new(row, 0, 0, 0, 1, 3) }, 1);
            history.Advance(.001);
        }
    }

    [Test]
    public void OverrangeGpuPackingIsFiniteAndRetainsTransientSign()
    {
        var history = new AudioHistory(1, 100);
        history.Post(new(new double[4], 4, DateTime.UnixEpoch)
            { GlobalEvidence = new(double.MaxValue, -double.MaxValue, double.NaN, double.PositiveInfinity, 1, 3) }, 1);
        history.Advance(0);
        Assert.That(history.GlobalSTBR, Is.EqualTo(new Vector4(0, -float.MaxValue, float.MaxValue, 0)));
    }

    sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}

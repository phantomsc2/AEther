using System;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class AudioHistoryTests
{
    [Test]
    public void ResidualIsOwnedAveragedAndClearedWithSpectrum()
    {
        var clock = new HistoryClock();
        var history = new AudioHistory(1, 100, timeProvider: clock);
        var residual = new[] { .2 };
        history.Post(new(new double[4], 4, DateTime.UnixEpoch) { ResidualRms = residual }, 1);
        residual[0] = .9; // The producer may recycle its storage immediately.
        history.Post(new(new double[4], 4, DateTime.UnixEpoch)
            { AnalysisRow = 1, ResidualRms = new[] { .6 } }, 1);
        history.Advance(.02);
        Assert.That(history.ResidualSpectrum[0][0], Is.EqualTo(.4f).Within(1e-7));
        Assert.That(history.ResidualSpectrum[1], Is.EqualTo(history.ResidualSpectrum[0]));
        clock.Milliseconds = 100;
        history.Advance(.1);
        Assert.That(history.ResidualSpectrum[0], Is.All.Zero);
        Assert.That(history.ResidualSpectrum[1], Is.All.Zero);
        history.Post(new(new double[4], 4, DateTime.UnixEpoch)
            { AnalysisRow = 2, DiscontinuityGeneration = 1 }, 1);
        history.Advance(.01);
        Assert.That(history.ResidualSpectrum[0], Is.All.Zero);
    }

    [Test]
    public void ResidualPreservesStereoAndSanitizesInvalidValues()
    {
        var history = new AudioHistory(2, 100, timeProvider: new HistoryClock());
        history.Post(new(new double[16], 8, DateTime.UnixEpoch)
            { ResidualRms = new[] { .2, double.NaN, .7, -1 } }, 2);
        history.Advance(.01);
        Assert.That(history.ResidualSpectrum[0], Is.EqualTo(new[] { .2f, 0f }));
        Assert.That(history.ResidualSpectrum[1], Is.EqualTo(new[] { .7f, 0f }));
    }

    [Test]
    public void DelayedRowsAfterCaptureDropRestorePresentationReserve()
    {
        var clock = new HistoryClock();
        var history = new AudioHistory(1, 100, timeProvider: clock);
        Post(0);
        clock.Milliseconds = 100;
        // One missing row plus analysis latency below the pause-reset threshold.
        Post(2);
        for (int row = 3; row < 300; row++)
        {
            clock.Milliseconds += 10;
            Post(row);
            Assert.That(history.NewestAge, Is.Zero, $"row {row}");
        }
        Assert.That(history.History[0].AsSpan(0, 4).ToArray(), Is.All.EqualTo(.5f),
            "Clock recovery must retain the rolling history.");
        clock.Milliseconds += 2000;
        history.Advance(2);
        Assert.That(history.NewestAge, Is.GreaterThan(history.TimeScale));

        void Post(long row)
        {
            history.Post(new(new[] { .5, .5, .5, .5 }, 4, DateTime.UnixEpoch) { AnalysisRow = row }, 1);
            history.Advance(.01);
        }
    }

    sealed class HistoryClock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    [Test]
    public void ComponentEnvelopesDependOnAudioRowsRatherThanRenderBatching()
    {
        var batched = new AudioHistory(1, 100);
        var individual = new AudioHistory(1, 100);
        for (int row = 0; row < 12; row++)
        {
            var input = new SampleEvent<double>(new[] { .5, row % 2 == 0 ? .1 : .3, .2, .5 }, 4, DateTime.Now)
                { AnalysisRow = row };
            batched.Post(input, 1);
            individual.Post(input, 1);
            individual.Advance(.01);
        }
        batched.Advance(.12);
        Assert.That(batched.Components, Is.EqualTo(individual.Components));
        Assert.That(batched.FractalEnergy, Is.EqualTo(individual.FractalEnergy));
        Assert.That(batched.Components.X, Is.InRange(0f, 1f));
        Assert.That(batched.Components.Y, Is.InRange(0f, 1f));
        Assert.That(batched.Components.Z, Is.InRange(0f, 1f));
        Assert.That(batched.FractalEnergy, Is.InRange(0f, 1f));
        var unchanged = batched.Components;
        batched.Advance(0);
        Assert.That(batched.Components, Is.EqualTo(unchanged));
    }
    [Test]
    public void FractalDepthDemandFollowsMovementSpeedFromZeroToTen()
    {
        var clock = new HistoryClock();
        var history = new AudioHistory(1, 100, timeProvider: clock);
        Assert.That(history.FractalDepth, Is.Zero);
        for (var row = 1; row <= 200; row++)
        {
            history.Post(new(new[] { .2, .2, .1, .5 }, 4, DateTime.Now) { AnalysisRow = row }, 1);
            history.Advance(.01);
            Assert.That(history.FractalDepth, Is.InRange(0f, 10f));
            Assert.That(history.FractalDepth,
                Is.EqualTo(10 * Math.Clamp(history.MusicalVelocity / .5, 0, 1)).Within(1e-6));
        }
        Assert.That(history.FractalDepth, Is.EqualTo(10).Within(.001));
        clock.Milliseconds += 1600;
        history.Advance(1.6);
        Assert.That(history.FractalDepth,
            Is.EqualTo(10 * Math.Clamp(history.MusicalVelocity / .5, 0, 1)).Within(.001));
        clock.Milliseconds += 20000;
        history.Advance(20);
        Assert.That(history.FractalDepth, Is.EqualTo(0).Within(.001));
    }

    [Test]
    public void SustainedHeavyAudioBuildsSpeedAndUnexcitedSilenceDoesNotDrift()
    {
        var history = new AudioHistory(1, 100);
        history.Advance(10);
        Assert.That(history.MusicalTime, Is.Zero);
        Assert.That(history.MusicalVelocity, Is.Zero);
        double earlySpeed = 0;
        for (var row = 0; row < 400; row++)
        {
            history.Post(new(new[] { .2, .2, .1, .5 }, 4, DateTime.Now) { AnalysisRow = row }, 1);
            history.Advance(.01);
            if (row == 1) earlySpeed = history.MusicalVelocity;
        }
        Assert.That(history.MusicalVelocity, Is.GreaterThan(5 * earlySpeed));
        // RMS=.5, power=.25, transient RMS=.2 => steady target 3.8.
        Assert.That(earlySpeed, Is.EqualTo(3.8 * (1 - Math.Exp(-.02 / .16))).Within(1e-7));
        Assert.That(history.MusicalVelocity, Is.EqualTo(3.8 * (1 - Math.Exp(-4 / .16))).Within(1e-7));
    }

    [Test]
    public void ResumeAfterPacketGapRestoresHistoryWithoutRestartingMusicalTime()
    {
        var history = new AudioHistory(1, 100);
        history.Post(new(new[] { .5, 0, 0, .5 }, 4, DateTime.Now) { AnalysisRow = 0 }, 1);
        history.Advance(.01);
        var musicalTime = history.MusicalTime;
        System.Threading.Thread.Sleep(250);
        history.Advance(.25);
        Assert.That(history.NewestAge, Is.GreaterThan(.15));
        // Resume with consecutive sample rows: the pause consumed no samples.
        history.Post(new(new[] { 0, .5, 0, .5 }, 4, DateTime.Now) { AnalysisRow = 1 }, 1);
        history.Advance(.01);
        Assert.That(history.NewestAge, Is.LessThan(.03));
        Assert.That(history.AvailableSpan, Is.LessThan(.03));
        Assert.That(history.History[0][0], Is.EqualTo(.5f), "A pause ages old history rather than erasing it.");
        Assert.That(history.History[0][5], Is.EqualTo(.5f));
        Assert.That(history.MusicalTime, Is.GreaterThan(musicalTime));
    }

    [Test]
    public void MusicalTravelDependsOnAudioRatherThanRenderTiming()
    {
        var clock = new HistoryClock();
        var batched = new AudioHistory(1, 100, timeProvider: clock);
        var individual = new AudioHistory(1, 100, timeProvider: clock);
        for (var row = 0; row < 40; row++)
        {
            var input = new SampleEvent<double>(new[] { .1, .2, .1, .4 }, 4, DateTime.Now)
                { AnalysisRow = row };
            batched.Post(input, 1);
            individual.Post(input, 1);
            individual.Advance(.01);
        }
        batched.Advance(.4);
        Assert.That(batched.MusicalTime, Is.GreaterThan(0));
        Assert.That(batched.MusicalTime, Is.EqualTo(individual.MusicalTime).Within(1e-10));
        var coasting = batched.MusicalTime;
        var velocity = batched.MusicalVelocity;
        batched.Post(new(new double[4], 4, DateTime.Now) { AnalysisRow = 40 }, 1);
        clock.Milliseconds = 1000;
        batched.Advance(1);
        Assert.That(batched.MusicalTime, Is.GreaterThan(coasting));
        Assert.That(batched.MusicalVelocity, Is.InRange(.5 * velocity, .6 * velocity));
        clock.Milliseconds += 7000;
        batched.Advance(7);
        Assert.That(batched.MusicalVelocity, Is.LessThan(.01 * velocity));
        Assert.That(batched.MusicalTime - coasting, Is.LessThan(1.6 * velocity));
    }

    [Test]
    public void PercussionAcceleratesTravelAtTheSameTotalMagnitude()
    {
        var tone = new AudioHistory(1, 100);
        var hit = new AudioHistory(1, 100);
        tone.Post(new(new[] { .5, 0, 0, .5 }, 4, DateTime.Now), 1);
        hit.Post(new(new[] { 0, .5, 0, .5 }, 4, DateTime.Now), 1);
        tone.Advance(.01);
        hit.Advance(.01);
        Assert.That(hit.MusicalTime, Is.GreaterThan(tone.MusicalTime));
        Assert.That(hit.Energy, Is.EqualTo(tone.Energy));
    }

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
    public void SpectrumAveragesRowsAndRetainsTheAggregateBetweenPackets()
    {
        var history = new AudioHistory(1, 100);
        history.Post(new(new[] { .8, .1, .6, .2 }, 4, DateTime.Now), 1);
        history.Post(new(new[] { .2, .9, .3, .7 }, 4, DateTime.Now) { AnalysisRow = 1 }, 1);
        history.Advance(0);
        Assert.That(history.Spectrum[0], Is.EqualTo(new[] { .5f, .5f, .45f, .45f }).Within(1e-7));
        history.Post(new(new[] { .1, .2, .3, .4 }, 4, DateTime.Now) { AnalysisRow = 2 }, 1);
        history.Advance(0);
        Assert.That(history.Spectrum[0], Is.EqualTo(new[] { .1f, .2f, .3f, .4f }));
        history.Advance(0);
        Assert.That(history.Spectrum[0], Is.EqualTo(new[] { .1f, .2f, .3f, .4f }));
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
    public void BacklogIsBoundedAndDuplicateRowsDoNotReplayAttacks()
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
        Assert.That(history.Spectrum[0], Is.All.EqualTo(.1f));
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

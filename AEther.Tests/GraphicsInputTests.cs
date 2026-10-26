using System;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class GraphicsInputTests
{
    static readonly SampleFormat Format = new(SampleType.Float32.Instance, 8000, 1);
    static AnalyzerOptions Options() => new() { MinFrequency = 100, MaxFrequency = 1000, TimeResolution = 100 };

    [Test]
    public void FilterImplementationAndTuningDoNotChangeGraphicsContract()
    {
        var options = Options();
        var expected = options.GetGraphicsInput(Format);
        options.UseSwift = false;
        options.WindowFunction = DFTWindow.Blackman;
        options.SIMDEnabled = false;
        options.SwiftWidthScale = 2;
        options.SwiftImbalance = .5;
        options.SmoothingEnabled = false;
        options.SmoothingMode = SmoothingMode.Complex;
        options.LogAccelerationIntensity *= 2;
        options.ComplexAccelerationIntensity *= 2;
        options.SmoothingNoiseFloor *= 2;
        options.FrequencyWindowOctaves = 3;
        options.TimeWindowSeconds = .3;
        options.InputGainDb = 0;
        options.BufferCapacity = 8;
        options.MaxInputAgeMilliseconds = 100;
        Assert.That(options.GetGraphicsInput(Format), Is.EqualTo(expected));
        Assert.That(options.GetGraphicsInput(new(SampleType.Int16.Instance, 8000, 2)), Is.EqualTo(expected),
            "The renderer's two-channel handoff is independent of input encoding/channel count.");
    }

    [Test]
    public void ContractIncludesDomainMeaningEvenWithUnchangedTextureWidth()
    {
        var options = Options();
        var original = options.GetGraphicsInput(Format);
        options.MinFrequency *= 2; options.MaxFrequency *= 2;
        var shifted = options.GetGraphicsInput(Format);
        Assert.That(shifted.Domain.Length, Is.EqualTo(original.Domain.Length));
        Assert.That(shifted, Is.Not.EqualTo(original));
        options.TimeResolution = 101;
        Assert.That(options.GetGraphicsInput(Format).AnalysisRate, Is.EqualTo(8000.0 / 79));
        options.HistorySeconds = 3;
        Assert.That(options.GetGraphicsInput(Format).HistorySeconds, Is.EqualTo(3));
    }

    [Test]
    public void NewAudioGenerationPreservesMonotonicClockAndDropsQueuedRows()
    {
        var clock = new Clock();
        var original = new AudioHistory(3, 100, 1, timeProvider: clock);
        original.Post(new(new double[12], 12, DateTime.UnixEpoch) { AnalysisRow = 1000 }, 1);
        clock.Milliseconds = 5000;
        var next = original.Restart(new(new Domain(100, 12, 4), 101, 2));
        Assert.That(next.Now, Is.EqualTo(original.Now));
        next.Advance(.01);
        Assert.That(next.History[0], Is.All.Zero);
        next.Post(new(new double[16], 16, DateTime.UnixEpoch) { AnalysisRow = 0 }, 1);
        next.Advance(.01);
        Assert.That(next.DroppedRows, Is.Zero);
    }

    sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
}

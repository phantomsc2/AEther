using System;
using System.Linq;
using NUnit.Framework;

namespace AEther.Tests;

public class SplitterTests
{
    static readonly Domain Domain = new(100, 8, 17);
    static AnalyzerOptions Options() => new()
    {
        SineGain = 1, TransientGain = 1, NoiseGain = 1, MedianLength = .5, MedianWidth = 1,
    };
    static double[] Flat(double amplitude) => Enumerable.Repeat(Math.Log10(amplitude), Domain.Length).ToArray();
    static double[] Output() => new double[Splitter.OutputStride * Domain.Length];

    [Test]
    public void FlatFieldSupportsAllThreeEvidenceChannelsIndependently()
    {
        var splitter = new Splitter(Domain, Options(), 10);
        var output = Output();
        splitter.Process(Flat(.4), output);
        for (var i = 0; i < output.Length; i += Splitter.OutputStride)
        {
            Assert.That(output.AsSpan(i, 4).ToArray(), Is.All.EqualTo(.4).Within(1e-15));
            Assert.That(output[i] + output[i + 1] + output[i + 2], Is.GreaterThan(output[i + 3]));
        }
    }

    [Test]
    public void BroadbandAttackDoesNotStealEvidenceFromEstablishedChord()
    {
        var splitter = new Splitter(Domain, Options(), 10);
        var input = Flat(0);
        input[6] = Math.Log10(.5); input[10] = Math.Log10(.7);
        var output = Output();
        for (var frame = 0; frame < 8; frame++) splitter.Process(input, output);
        Array.Fill(input, Math.Log10(2));
        input[6] = Math.Log10(2.5); input[10] = Math.Log10(2.7);
        splitter.Process(input, output);
        Assert.That(output[4 * 6], Is.EqualTo(.5).Within(1e-15));
        Assert.That(output[4 * 10], Is.EqualTo(.7).Within(1e-15));
        Assert.That(output[4 * 6 + 1], Is.EqualTo(2).Within(1e-15));
        Assert.That(output[4 * 10 + 1], Is.EqualTo(2).Within(1e-15));
        Assert.That(output[4 * 6 + 3], Is.EqualTo(2.5).Within(1e-15));
    }

    [Test]
    public void IsolatedOutlierIsRejectedByRobustAxisEstimatesButPreservedInRawAmplitude()
    {
        var splitter = new Splitter(Domain, Options(), 10);
        var output = Output();
        for (var frame = 0; frame < 5; frame++) splitter.Process(Flat(.1), output);
        var input = Flat(.1); input[8] = Math.Log10(100);
        splitter.Process(input, output);
        Assert.That(output.AsSpan(4 * 8, 3).ToArray(), Is.All.EqualTo(.1).Within(1e-15));
        Assert.That(output[4 * 8 + 3], Is.EqualTo(100));
    }

    [Test]
    public void BothAxesEstimateUsesFullRectangleRatherThanMedianOfMedians()
    {
        var options = Options(); options.MedianLength = .3; options.MedianWidth = .3;
        var splitter = new Splitter(new Domain(100, 8, 3), options, 10);
        var output = new double[12];
        splitter.Process(new[] { double.NegativeInfinity, double.NegativeInfinity, 0.0 }, output);
        splitter.Process(new[] { double.NegativeInfinity, double.NegativeInfinity, 0.0 }, output);
        splitter.Process(new[] { 0.0, 0.0, 0.0 }, output);
        Assert.That(output[4], Is.Zero, "Time median of the middle column");
        Assert.That(output[5], Is.EqualTo(1), "Frequency median of the newest row");
        Assert.That(output[6], Is.EqualTo(1), "Five of nine samples are one; median-of-medians would be zero");
    }

    [TestCase(5, .3, .3, 10)]
    [TestCase(8, .5, 3, 10)]
    [TestCase(1, .5, 1, 10)]
    [TestCase(17, 1.1, 1, 10)]
    [TestCase(65, .2, 3, 256.684)]
    public void EvidenceMatchesSortedNeighborhoodQuantilesAcrossStartupAndRingWrap(
        int bands, double seconds, double octaves, double rate)
    {
        var domain = new Domain(100, 8, bands);
        var options = Options(); options.MedianLength = seconds; options.MedianWidth = octaves;
        var length = (int)Math.Round(seconds * rate) | 1;
        var frequencyWidth = Math.Min((int)Math.Round(octaves * domain.Resolution) | 1, bands);
        var splitter = new Splitter(domain, options, rate);
        var history = new System.Collections.Generic.List<double[]>();
        var output = new double[4 * domain.Length];
        var random = new Random(123);
        for (var frame = 0; frame < 2 * length + 7; frame++)
        {
            // Include repeated values and exact zeros as well as continuous random magnitudes.
            var input = Enumerable.Range(0, domain.Length).Select(_ => frame % 3 == 0
                ? random.Next(4) == 0 ? double.NegativeInfinity : -random.Next(4)
                : -3 * random.NextDouble()).ToArray();
            var amplitudes = input.Select(x => Math.Pow(10, x)).ToArray();
            history.Add(amplitudes);
            if (history.Count > length) history.RemoveAt(0);
            splitter.Process(input, output);
            for (var k = 0; k < domain.Length; k++)
            {
                var first = Math.Max(0, k - frequencyWidth / 2);
                var width = Math.Min(domain.Length - 1, k + frequencyWidth / 2) - first + 1;
                var time = history.Select(row => row[k]).Order().ToArray();
                var frequency = amplitudes.Skip(first).Take(width).Order().ToArray();
                var both = history.SelectMany(row => row.Skip(first).Take(width)).Order().ToArray();
                Assert.That(output[4 * k], Is.EqualTo(time[(time.Length - 1) / 2]));
                Assert.That(output[4 * k + 1], Is.EqualTo(frequency[(frequency.Length - 1) / 2]));
                Assert.That(output[4 * k + 2], Is.EqualTo(both[(both.Length - 1) / 2]));
                Assert.That(output[4 * k + 3], Is.EqualTo(amplitudes[k]));
            }
        }
    }

    [Test]
    public void ActualRateDefinesFiniteHistoryAndSilenceEventuallyClearsAllEvidence()
    {
        var splitter = new Splitter(Domain, Options(), 10); // Five-frame time window.
        var output = Output();
        for (var frame = 0; frame < 8; frame++) splitter.Process(Flat(1), output);
        for (var frame = 0; frame < 2; frame++)
        {
            splitter.Process(Flat(0), output);
            Assert.That(output[0], Is.EqualTo(1), "Time evidence may retain finite history");
            Assert.That(output[1], Is.Zero);
            Assert.That(output[3], Is.Zero, "Raw amplitude must follow silence immediately");
        }
        splitter.Process(Flat(0), output);
        Assert.That(output, Is.All.Zero);
    }

    [Test]
    public void FrequencyAndTimeEdgesUseAvailableSamplesWithoutInventedPadding()
    {
        var splitter = new Splitter(Domain, Options(), 10);
        var output = Output();
        splitter.Process(Flat(.25), output);
        Assert.That(output, Is.All.EqualTo(.25));
        var narrow = new Splitter(new Domain(100, 8, 1), Options(), 10);
        var single = new double[4]; narrow.Process(new[] { Math.Log10(.5) }, single);
        Assert.That(single, Is.All.EqualTo(.5));
    }

    [Test]
    public void IndependentGainsDoNotChangeOtherEvidenceOrRawAmplitude()
    {
        var a = new Splitter(Domain, Options(), 10);
        var options = Options(); options.SineGain = 2; options.TransientGain = 7; options.NoiseGain = 0;
        var b = new Splitter(Domain, options, 10);
        var x = Output(); var y = Output(); var random = new Random(42);
        for (var frame = 0; frame < 30; frame++)
        {
            var input = Enumerable.Range(0, Domain.Length).Select(_ => -4 * random.NextDouble()).ToArray();
            a.Process(input, x); b.Process(input, y);
            for (var i = 0; i < x.Length; i += 4)
            {
                Assert.That(y[i], Is.EqualTo(2 * x[i]));
                Assert.That(y[i + 1], Is.EqualTo(7 * x[i + 1]));
                Assert.That(y[i + 2], Is.Zero);
                Assert.That(y[i + 3], Is.EqualTo(x[i + 3]));
            }
        }
    }

    [Test]
    public void InvalidInputIsZeroAndQuietAmplitudeIsNotNormalizedAway()
    {
        var splitter = new Splitter(Domain, Options(), 10);
        var input = Flat(0); input[0] = double.NaN; input[1] = double.PositiveInfinity; input[2] = 309;
        var output = Output(); splitter.Process(input, output);
        Assert.That(output, Is.All.Zero);
        for (var frame = 0; frame < 5; frame++) splitter.Process(Flat(1e-10), output);
        Assert.That(output, Is.All.EqualTo(1e-10).Within(1e-24));
    }

    [Test]
    public void RejectsDimensionsBeforeUpdatingHistory()
    {
        var splitter = new Splitter(Domain, Options(), 10);
        var output = Output();
        Assert.Throws<ArgumentException>(() => splitter.Process(new double[1], output));
        Assert.Throws<ArgumentException>(() => splitter.Process(Flat(1), new double[1]));
        splitter.Process(Flat(.25), output);
        Assert.That(output, Is.All.EqualTo(.25));
    }

    [Test]
    public void RejectsInvalidGains([Values(0, 1, 2)] int component,
        [Values(-1.0, double.NaN, double.PositiveInfinity)] double gain)
    {
        var options = Options();
        if (component == 0) options.SineGain = gain;
        else if (component == 1) options.TransientGain = gain;
        else options.NoiseGain = gain;
        Assert.Throws<ArgumentOutOfRangeException>(() => new Splitter(Domain, options));
    }
}

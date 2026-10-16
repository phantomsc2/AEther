using System;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class DecompositionTests
{
    static readonly Domain Domain = new(100, 8, 17);
    static AnalyzerOptions Options(double amount = .5) => new()
    {
        SineGain = 1, TransientGain = 1, NoiseGain = 1,
        MedianLength = .2, MedianWidth = 1, SmoothingAmount = amount,
    };

    [Test]
    public void BilateralDecompositionReconstructsAndProducesBothResidualSigns()
    {
        var splitter = new Splitter(Domain, Options(), 100);
        var input = new double[Domain.Length];
        var output = new double[Domain.Length * Splitter.OutputStride];
        bool positive = false, negative = false;
        for (var frame = 0; frame < 50; frame++)
        {
            // Small variations in a diffuse field: all bins stay in the noise class.
            for (var k = 0; k < input.Length; k++)
                input[k] = Math.Log10(.4 + .03 * Math.Sin(k * 2 + frame));
            splitter.Process(input, output);
            for (var k = 0; k < input.Length; k++)
            {
                var i = k * Splitter.OutputStride;
                Assert.That(output.AsSpan(i, 3).ToArray(), Is.All.GreaterThanOrEqualTo(0));
                Assert.That(output[i] + output[i + 1] + output[i + 2] + output[i + 4],
                    Is.EqualTo(output[i + 3]).Within(1e-14));
                positive |= output[i + 4] > 1e-4;
                negative |= output[i + 4] < -1e-4;
            }
        }
        Assert.That(positive && negative, Is.True);
        Array.Fill(input, double.NegativeInfinity);
        splitter.Process(input, output);
        Assert.That(output, Is.All.Zero, "Active filter history must not relight silence.");
    }

    [Test]
    public void DisplayGainsDoNotChangeSmoothingOrResidual()
    {
        var options = Options();
        var raw = new Splitter(Domain, options, 100);
        options.SineGain = 2; options.TransientGain = 3; options.NoiseGain = 4;
        var gained = new Splitter(Domain, options, 100);
        var a = new double[Domain.Length * Splitter.OutputStride];
        var b = new double[a.Length];
        var random = new Random(14);
        for (var frame = 0; frame < 30; frame++)
        {
            var input = Enumerable.Range(0, Domain.Length).Select(_ => -2 * random.NextDouble()).ToArray();
            raw.Process(input, a); gained.Process(input, b);
            for (var i = 0; i < a.Length; i += Splitter.OutputStride)
            {
                for (var c = 0; c < 3; c++) Assert.That(b[i + c], Is.EqualTo((c + 2) * a[i + c]));
                Assert.That(b[i + 3], Is.EqualTo(a[i + 3]));
                Assert.That(b[i + 4], Is.EqualTo(a[i + 4]));
            }
        }
    }

    [Test]
    public void BypassRetainsOriginalPartitionAndZeroResidual()
    {
        var splitter = new Splitter(Domain, Options(0), 100);
        var output = new double[Domain.Length * Splitter.OutputStride];
        splitter.Process(Enumerable.Repeat(Math.Log10(.4), Domain.Length).ToArray(), output);
        for (var i = 0; i < output.Length; i += Splitter.OutputStride)
        {
            Assert.That(output[i + 2], Is.EqualTo(.4).Within(1e-15));
            Assert.That(output[i + 4], Is.Zero.Within(1e-15));
        }
    }

    [Test]
    public void RendererRetainsSignedResidualWithoutChangingOriginalMagnitude()
    {
        var history = new AudioHistory(1, 100);
        history.Post(new(new[] { .1, .2, .3, .4, -.2 }, 5, DateTime.Now), 1);
        history.Advance(0);
        Assert.That(history.ResidualHistory[0][0], Is.EqualTo(-.2f));
        Assert.That(history.ResidualHistory[1][0], Is.EqualTo(-.2f));
        Assert.That(history.ResidualSpectrum[0][0], Is.EqualTo(-.2f));
        Assert.That(history.History[0][3], Is.EqualTo(.4f));
        Assert.That(history.Energy, Is.EqualTo(.4f));
    }

    [TestCase(-.1, 6)]
    [TestCase(1.1, 6)]
    [TestCase(double.NaN, 6)]
    [TestCase(.5, 0)]
    [TestCase(.5, double.PositiveInfinity)]
    public void RejectsInvalidSmoothing(double amount, double edge)
    {
        var options = Options(amount); options.SmoothingEdgeDb = edge;
        Assert.Throws<ArgumentOutOfRangeException>(() => new Splitter(Domain, options, 100));
    }
}

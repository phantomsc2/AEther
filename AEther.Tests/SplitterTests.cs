using System;
using System.Linq;
using NUnit.Framework;

namespace AEther.Tests
{
    public class SplitterTests
    {
        static readonly Domain Domain = new(27.5, 8, 65);
        static double[] Flat(double magnitude) => Enumerable.Repeat(Math.Log10(magnitude), Domain.Length).ToArray();
        static Splitter Create() => new(Domain, new AnalyzerOptions { SineGain = 1, TransientGain = 1, NoiseGain = 1, MedianLength = .5, MedianWidth = 1 }, 10);

        [TestCase(.5, 0)]
        [TestCase(.7, 0)]
        [TestCase(.725, .1464466094067262)]
        [TestCase(.75, .5)]
        [TestCase(.775, .8535533905932737)]
        [TestCase(.8, 1)]
        [TestCase(1, 1)]
        public void SineMaskMatchesPaperRaisedCosine(double evidence, double membership)
        {
            var splitter = Create();
            var input = Flat(1 - evidence);
            input[30] = Math.Log10(evidence);
            var output = new double[4 * Domain.Length];
            splitter.Process(input, output);
            Assert.That(output[120], Is.EqualTo(evidence * membership).Within(1e-14));
            Assert.That(output[121], Is.Zero);
            Assert.That(output[122], Is.EqualTo(evidence * (1 - membership)).Within(1e-14));
        }

        [TestCase(.5, 0)]
        [TestCase(.7, 0)]
        [TestCase(.725, .1464466094067262)]
        [TestCase(.75, .5)]
        [TestCase(.775, .8535533905932737)]
        [TestCase(.8, 1)]
        [TestCase(1, 1)]
        public void TransientMaskMatchesPaperRaisedCosine(double evidence, double membership)
        {
            var splitter = Create();
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 8; ++n) splitter.Process(Flat(1 - evidence), output);
            splitter.Process(Flat(evidence), output);
            Assert.That(output[120], Is.Zero);
            Assert.That(output[121], Is.EqualTo(evidence * membership).Within(1e-14));
            Assert.That(output[122], Is.EqualTo(evidence * (1 - membership)).Within(1e-14));
        }

        [Test]
        public void FlatSpectrumIsNoiseAndSustainedNarrowRidgeIsSine()
        {
            var splitter = Create();
            var output = new double[4 * Domain.Length];
            var input = Flat(.01);
            input[30] = Math.Log10(.5);
            for (var n = 0; n < 20; ++n) splitter.Process(input, output);
            Assert.That(output[120], Is.EqualTo(.5).Within(1e-14));
            Assert.That(output[121], Is.Zero);
            Assert.That(output[122], Is.Zero);
            Assert.That(output[2], Is.EqualTo(.01).Within(1e-14));
        }

        [Test]
        public void CausalMedianUsesActualRateAndAttackSettlesWithoutReleaseTail()
        {
            var splitter = Create(); // 0.5 s at 10 Hz = five frames, not the options' 256 Hz.
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 8; ++n) splitter.Process(Flat(.01), output);
            for (var n = 0; n < 2; ++n)
            {
                splitter.Process(Flat(1), output);
                Assert.That(output[121], Is.EqualTo(1));
            }
            splitter.Process(Flat(1), output);
            Assert.That(output[121], Is.Zero);
            Assert.That(output[122], Is.EqualTo(1));
            splitter.Process(Flat(0), output);
            Assert.That(output, Is.All.Zero, "median evidence must not retain output after input stops");
        }

        [Test]
        public void ComponentsSumToCurrentMagnitudeAcrossMixedHistoryAndFrequencyEdges()
        {
            var splitter = Create();
            var random = new Random(789);
            var input = new double[Domain.Length];
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 150; ++n)
            {
                for (var k = 0; k < input.Length; ++k)
                    input[k] = random.NextDouble() < .1 ? double.NegativeInfinity : -12 + 14 * random.NextDouble();
                splitter.Process(input, output);
                for (var k = 0; k < input.Length; ++k)
                {
                    var magnitude = Math.Pow(10, input[k]);
                    var i = 4 * k;
                    Assert.That(output.AsSpan(i, 3).ToArray(), Is.All.InRange(0, magnitude));
                    Assert.That(output[i] + output[i + 1] + output[i + 2],
                        Is.EqualTo(magnitude).Within(Math.Max(1e-300, magnitude * 1e-14)));
                    Assert.That(output[i + 3], Is.EqualTo(magnitude));
                }
            }
        }

        [Test]
        public void IndependentGainsScaleOnlyTheirClassesAndLeaveMagnitudeUnscaled()
        {
            var quiet = Create();
            var loud = new Splitter(Domain, new AnalyzerOptions { SineGain = 2, TransientGain = 7, NoiseGain = 0, MedianLength = .5, MedianWidth = 1 }, 10);
            var random = new Random(42);
            var input = new double[Domain.Length];
            var gained = new double[input.Length];
            var a = new double[4 * input.Length];
            var b = new double[a.Length];
            for (var n = 0; n < 100; ++n)
            {
                for (var k = 0; k < input.Length; ++k)
                {
                    input[k] = -8 + 6 * random.NextDouble();
                    gained[k] = input[k];
                }
                quiet.Process(input, a);
                loud.Process(gained, b);
                for (var i = 0; i < a.Length; i += 4)
                {
                    Assert.That(b[i], Is.EqualTo(2 * a[i]).Within(1e-12));
                    Assert.That(b[i + 1], Is.EqualTo(7 * a[i + 1]).Within(1e-12));
                    Assert.That(b[i + 2], Is.Zero);
                    Assert.That(b[i + 3], Is.EqualTo(a[i + 3]));
                }
            }
        }

        [Test]
        public void ZeroEvidenceAndInvalidValuesStayFiniteAndQuietValuesAreNotFloored()
        {
            var splitter = Create();
            var input = Flat(0);
            var output = new double[4 * Domain.Length];
            splitter.Process(input, output);
            Assert.That(output, Is.All.Zero);
            input[0] = double.NaN;
            input[1] = double.PositiveInfinity;
            input[2] = 309; // linear conversion overflows
            splitter.Process(input, output);
            Assert.That(output, Is.All.Zero);
            Array.Fill(input, -10);
            splitter.Process(input, output);
            Assert.That(output[3], Is.EqualTo(1e-10));
            Assert.That(output[0] + output[1] + output[2], Is.EqualTo(1e-10).Within(1e-24));
        }

        [Test]
        public void RejectsDimensionsBeforeUpdatingHistory()
        {
            var splitter = Create();
            Assert.Throws<ArgumentException>(() => splitter.Process(new double[1], new double[4 * Domain.Length]));
            Assert.Throws<ArgumentException>(() => splitter.Process(Flat(1), new double[1]));
        }

        [Test]
        public void RejectsInvalidClassGains([Values(0, 1, 2)] int component,
            [Values(-1.0, double.NaN, double.PositiveInfinity)] double gain)
        {
            var options = new AnalyzerOptions();
            if (component == 0) options.SineGain = gain;
            else if (component == 1) options.TransientGain = gain;
            else options.NoiseGain = gain;
            Assert.Throws<ArgumentOutOfRangeException>(() => new Splitter(Domain, options));
        }

        [TestCase(.4, .8)]
        [TestCase(.7, .7)]
        [TestCase(.8, .7)]
        [TestCase(.7, 1.1)]
        [TestCase(double.NaN, .8)]
        [TestCase(.7, double.PositiveInfinity)]
        public void RejectsInvalidOrOverlappingMaskBounds(double lower, double upper)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Splitter(Domain,
                new AnalyzerOptions { FuzzyLower = lower, FuzzyUpper = upper }));
        }
    }
}

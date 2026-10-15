using System;
using System.Linq;
using NUnit.Framework;

namespace AEther.Tests
{
    public class SplitterDesignTests
    {
        static readonly Domain Domain = new(27.5, 8, 65);
        static AnalyzerOptions Fixed() => new() { AdaptiveSpectrumRange = false, JitterLength = 0, SinuoidLength = .1, TransientLength = .1, NoiseLength = .1, SinuoidWidth = 1, TransientWidth = 1, NoiseWidth = 1 };

        [Test]
        public void ChordRidgesSwitchToTheirNewLevelWithoutAnEmaRamp()
        {
            var splitter = new Splitter(Domain, Fixed());
            var input = Enumerable.Repeat(-3.0, Domain.Length).ToArray();
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 100; ++n) splitter.Process(input, output);
            foreach (var k in new[] { 10, 26, 42 }) input[k] = -1;
            var first = -1;
            var expected = .6 * Math.Sqrt((.01 - .000001) / (.01 - .00000001));
            for (var n = 0; n < 30; ++n)
            {
                splitter.Process(input, output);
                foreach (var k in new[] { 10, 26, 42 })
                    Assert.That(output[4 * k], Is.EqualTo(0).Or.EqualTo(expected).Within(1e-12));
                if (output[40] > 0 && first < 0) first = n;
                Assert.That(output.Where((v, i) => i % 4 == 1), Is.All.Zero, "isolated chord ridges became a broadband attack");
            }
            Assert.That(first, Is.InRange(1, 16), "100 ms median accepts a chord in about half its window");
            Assert.That(output[40], Is.EqualTo(expected).Within(1e-12));
        }

        [Test]
        public void IsolatedSpikesBecomeResidualButBroadbandStepsAttackAndThenSettle()
        {
            var splitter = new Splitter(Domain, Fixed());
            var input = Enumerable.Repeat(-3.0, Domain.Length).ToArray();
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 40; ++n) splitter.Process(input, output);
            input[30] = -1;
            splitter.Process(input, output);
            Assert.That(output.Where((v, i) => i % 4 is 0 or 1), Is.All.Zero);
            Assert.That(splitter.ResidualDb.Span[30], Is.GreaterThan(0));
            Array.Fill(input, -3.0);
            splitter.Process(input, output);
            Assert.That(output.Where((v, i) => i % 4 is 0 or 1), Is.All.Zero);
            Array.Fill(input, -1.0);
            splitter.Process(input, output);
            splitter.Process(input, output);
            Assert.That(output[4 * 30 + 1], Is.GreaterThan(.3));
            for (var n = 0; n < 40; ++n) splitter.Process(input, output);
            Assert.That(output.Where((v, i) => i % 4 is 0 or 1), Is.All.Zero);
            Array.Fill(input, -3.0);
            for (var n = 0; n < 30; ++n)
            {
                splitter.Process(input, output);
                Assert.That(output.Where((v, i) => i % 4 == 1), Is.All.Zero, "a falling level generated an attack");
            }
        }

        [Test]
        public void SharedQuantilesConvergeAndFreezeInSilenceWithoutCollapsingTheRange()
        {
            var range = new SpectralRange(2, .01, -80);
            var spectrum = Enumerable.Repeat(-70.0, 64).ToArray();
            spectrum[0] = spectrum[1] = -20;
            for (var n = 0; n < 15000; ++n) range.Process(spectrum);
            Assert.That(range.FloorDb, Is.EqualTo(-70).Within(.1));
            Assert.That(range.CeilingDb, Is.EqualTo(-20).Within(.1));
            var bounds = (range.FloorDb, range.CeilingDb);
            Array.Fill(spectrum, -200.0);
            for (var n = 0; n < 10000; ++n) range.Process(spectrum);
            Assert.That((range.FloorDb, range.CeilingDb), Is.EqualTo(bounds));
            Array.Fill(spectrum, -40.0);
            for (var n = 0; n < 15000; ++n) range.Process(spectrum);
            Assert.That(range.SpanDb, Is.GreaterThanOrEqualTo(24));
        }

        [Test]
        public void QuantileAdaptationCannotCreateAttacksOrRidgesFromAFlatSteadySpectrum()
        {
            var splitter = new Splitter(Domain, new AnalyzerOptions { QuantileHistorySeconds = 2 });
            var input = Enumerable.Repeat(-2.0, Domain.Length).ToArray();
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 30000; ++n)
            {
                splitter.Process(input, output);
                Assert.That(output.Where((v, i) => i % 4 is 0 or 1), Is.All.Zero);
            }
            Assert.That(output[3], Is.EqualTo(.6).Within(1e-12), "raw level changed with display normalization");
        }

        [TestCase(SplitterOrder.TimeThenFrequency)]
        [TestCase(SplitterOrder.FrequencyThenTime)]
        public void InvalidAndInaudibleInputCannotBeAmplifiedByNormalization(SplitterOrder order)
        {
            var splitter = new Splitter(Domain, new AnalyzerOptions { SplitOrder = order });
            var input = Enumerable.Repeat(double.NaN, Domain.Length).ToArray();
            var output = new double[4 * Domain.Length];
            splitter.Process(input, output);
            Assert.That(output, Is.All.Zero);
            Array.Fill(input, -9.0);
            input[32] = -8;
            for (var n = 0; n < 100; ++n) splitter.Process(input, output);
            Assert.That(output, Is.All.Zero);
        }

        [Test]
        public void SustainedBroadbandEnergyIsNoiseAndSingleBinAttacksAreDiscarded()
        {
            foreach (var order in Enum.GetValues<SplitterOrder>())
            {
                var options = Fixed(); options.SplitOrder = order;
                var splitter = new Splitter(Domain, options);
                var input = Enumerable.Repeat(-4.0, Domain.Length).ToArray();
                var output = new double[4 * Domain.Length];
                for (var n = 0; n < 50; ++n) splitter.Process(input, output);
                input[30] = -1;
                splitter.Process(input, output);
                Assert.That(splitter.ResidualDb.Span[30], Is.EqualTo(60));
                Assert.That(output, Is.All.Zero, "pure residual entered visible channels");
                Array.Fill(input, -2.0);
                for (var n = 0; n < 50; ++n) splitter.Process(input, output);
                Assert.That(output.Where((v, i) => i % 4 is 0 or 1), Is.All.Zero);
                Assert.That(output[4 * 30 + 2], Is.EqualTo(.4).Within(1e-12));
            }
        }

        [Test]
        public void ResidualDoesNotAdvanceVisualEnergyClockOrTriggerLightning()
        {
            var splitter = new Splitter(Domain, Fixed());
            var controls = new RetainedVisualFeatures(Domain, 1, 256);
            var input = Enumerable.Repeat(-4.0, Domain.Length).ToArray();
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 40; ++n) splitter.Process(input, output);
            input[30] = -1;
            splitter.Process(input, output);
            var features = controls.Process(new(output, output.Length, DateTime.Now));
            Assert.That(features.Level, Is.Zero);
            Assert.That(features.MusicalTime, Is.Zero);
            Assert.That(controls.Transient, Is.Zero);
            Assert.That(splitter.ResidualDb.Span[30], Is.GreaterThan(0));
        }

        [Test]
        public void MedianOrdersDifferOnMixedStructureAndBothStayNonnegative()
        {
            var firstOptions = Fixed();
            var secondOptions = Fixed(); secondOptions.SplitOrder = SplitterOrder.FrequencyThenTime;
            var first = new Splitter(Domain, firstOptions);
            var second = new Splitter(Domain, secondOptions);
            var input = new double[Domain.Length];
            var a = new double[4 * Domain.Length]; var b = new double[a.Length];
            var random = new Random(389);
            double difference = 0;
            for (var n = 0; n < 100; ++n)
            {
                for (var k = 0; k < input.Length; ++k) input[k] = -3 + random.NextDouble() * 2;
                first.Process(input, a); second.Process(input, b);
                for (var i = 0; i < a.Length; ++i)
                {
                    Assert.That(a[i], Is.InRange(0, 1)); Assert.That(b[i], Is.InRange(0, 1));
                    difference += Math.Abs(a[i] - b[i]);
                }
            }
            Assert.That(difference, Is.GreaterThan(1), "nonlinear filter ordering was ignored");
        }

        [TestCase(SplitterOrder.TimeThenFrequency)]
        [TestCase(SplitterOrder.FrequencyThenTime)]
        public void ComponentRatiosExpressPowerRatherThanDistanceFromTheDbFloor(SplitterOrder order)
        {
            var options = Fixed(); options.SplitOrder = order;
            var splitter = new Splitter(Domain, options);
            var input = Enumerable.Repeat(-3.0, Domain.Length).ToArray(); // -60 dB bed
            input[30] = -2; // -40 dB tone: 100 times the bed's power
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 50; ++n) splitter.Process(input, output);
            var red = output[120]; var blue = output[122];
            Assert.That(red * red / (red * red + blue * blue), Is.GreaterThan(.99));
            Assert.That(output[121], Is.Zero);
            // The shared brightness changes with gain; the component power shares do not.
            Array.Fill(input, -2); input[30] = -1;
            for (var n = 0; n < 50; ++n) splitter.Process(input, output);
            red = output[120]; blue = output[122];
            Assert.That(red * red / (red * red + blue * blue), Is.EqualTo(.99).Within(.0001));
            Array.Fill(input, -1);
            for (var n = 0; n < 50; ++n) splitter.Process(input, output);
            Assert.That(output[120], Is.Zero);
            Assert.That(output[121], Is.Zero);
            Assert.That(output[122], Is.GreaterThan(0), "pure broadband passages must stay blue");
        }

        [Test]
        public void BroadbandAttackDominatesItsQuieterBedImmediatelyAndThenBecomesNoise()
        {
            var splitter = new Splitter(Domain, Fixed());
            var input = Enumerable.Repeat(-3.0, Domain.Length).ToArray();
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 50; ++n) splitter.Process(input, output);
            Array.Fill(input, -2.0);
            splitter.Process(input, output);
            var green = output[121]; var blue = output[122];
            Assert.That(green * green / (green * green + blue * blue), Is.GreaterThan(.99));
            for (var n = 0; n < 50; ++n) splitter.Process(input, output);
            Assert.That(output[121], Is.Zero);
            Assert.That(output[122], Is.GreaterThan(0));
        }

        [Test]
        public void DefaultsUseShortJitterAndImmediateHistoryPlayhead()
        {
            Assert.That(new AnalyzerOptions().JitterLength, Is.EqualTo(.01));
            var scroll = new HistoryScroll(256);
            for (var i = 0; i < 10; ++i) scroll.AddRow();
            scroll.Advance(0);
            Assert.That(scroll.Position, Is.EqualTo(9));
        }

        [TestCase(256)]
        [TestCase(1000)]
        public void PostFilterSmoothsPowerAtActualRateWithoutDelayingTransientPeaks(int rate)
        {
            var rawOptions = Fixed();
            var postOptions = Fixed(); postOptions.JitterLength = .01;
            var raw = new Splitter(Domain, rawOptions, rate);
            var post = new Splitter(Domain, postOptions, rate);
            var input = Enumerable.Repeat(-3.0, Domain.Length).ToArray();
            var a = new double[4 * Domain.Length]; var b = new double[a.Length];
            var decay = Math.Exp(-1 / (rate * .01));
            double h = 0, t = 0, noise = 0;
            double Power(double[] output, int component)
            {
                var k = 120;
                var sum = output[k] * output[k] + output[k + 1] * output[k + 1] + output[k + 2] * output[k + 2];
                return sum == 0 ? 0 : (Math.Pow(10, 10 * (output[k + 3] - 1)) - 1e-8) * output[k + component] * output[k + component] / sum;
            }
            for (var n = 0; n < 400; ++n)
            {
                if (n == 120) Array.Fill(input, -2.0);
                if (n == 160) input[30] = -1;
                if (n == 300) Array.Fill(input, -4.0);
                raw.Process(input, a); post.Process(input, b);
                h = n == 0 ? Power(a, 0) : decay * h + (1 - decay) * Power(a, 0);
                noise = n == 0 ? Power(a, 2) : decay * noise + (1 - decay) * Power(a, 2);
                t = Math.Max(Power(a, 1), decay * t);
                Assert.That(Power(b, 0), Is.EqualTo(h).Within(1e-10));
                Assert.That(Power(b, 1), Is.EqualTo(t).Within(1e-10));
                Assert.That(Power(b, 2), Is.EqualTo(noise).Within(1e-10));
                Assert.That(post.ResidualDb.ToArray(), Is.EqualTo(raw.ResidualDb.ToArray()), "post-filter tails changed residual classification");
            }
        }

        [Test]
        public void IndependentDefaultWindowsGiveRidgesLongerMemoryThanAttacks()
        {
            var options = new AnalyzerOptions { AdaptiveSpectrumRange = false, JitterLength = 0 };
            var splitter = new Splitter(Domain, options);
            var input = Enumerable.Repeat(-4.0, Domain.Length).ToArray();
            var output = new double[4 * Domain.Length];
            for (var n = 0; n < 100; ++n) splitter.Process(input, output);
            input[30] = -1;
            for (var n = 0; n < 30; ++n) splitter.Process(input, output);
            Assert.That(output[120], Is.Zero);
            for (var n = 0; n < 15; ++n) splitter.Process(input, output);
            Assert.That(output[120], Is.GreaterThan(0));
            Assert.That(output[121], Is.Zero);
            Array.Fill(input, -1);
            splitter.Process(input, output);
            Assert.That(output[121], Is.GreaterThan(0));
            for (var n = 0; n < 6; ++n) splitter.Process(input, output);
            Assert.That(output[121], Is.Zero, "30 ms attack baseline should settle before the 100 ms noise baseline");
            for (var n = 0; n < 40; ++n) splitter.Process(input, output);
            Assert.That(output[120], Is.Zero);
            Assert.That(output[122], Is.GreaterThan(0));
        }
    }
}

using System;
using System.Linq;
using NUnit.Framework;

namespace AEther.Tests
{
    public class SplitterTests
    {
        [Test]
        public void SilenceHasNoTonalOrTransientEnergyIncludingAfterWarmup()
        {
            var domain = new Domain(27.5, 8, 65);
            var splitter = new Splitter(domain, new AnalyzerOptions());
            var input = Enumerable.Repeat(-10.0, domain.Length).ToArray();
            var output = new double[4 * domain.Length];
            for (var frame = 0; frame < 200; ++frame)
            {
                splitter.Process(input, output);
                Assert.That(output, Is.All.Zero, $"silence frame {frame}");
            }
        }

        [Test]
        public void SustainedPeakIsRedAndBroadbandAttackIsGreenThenSettles()
        {
            var domain = new Domain(27.5, 8, 65);
            var splitter = new Splitter(domain, new AnalyzerOptions());
            var input = Enumerable.Repeat(-3.0, domain.Length).ToArray();
            var output = new double[4 * domain.Length];
            for (var frame = 0; frame < 80; ++frame)
                splitter.Process(input, output);
            input[30] = -1;
            for (var frame = 0; frame < 80; ++frame)
                splitter.Process(input, output);
            Assert.That(output[4 * 30], Is.GreaterThan(0));
            Assert.That(output[4 * 30 + 1], Is.Zero);
            for (var bin = 0; bin < input.Length; ++bin)
                input[bin] += 1;
            splitter.Process(input, output);
            splitter.Process(input, output);
            Assert.That(output.Where((_, i) => i % 4 == 1).Max(), Is.GreaterThan(0));
            for (var frame = 0; frame < 80; ++frame)
                splitter.Process(input, output);
            Assert.That(output.Where((_, i) => i % 4 == 1).Max(), Is.LessThan(1e-6), "exponential release should settle below visible levels");
        }
    }
}

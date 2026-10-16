using System;
using System.Linq;
using NUnit.Framework;

namespace AEther.Tests
{
    public class SpectrumStabilityTests
    {
        [Test]
        public void SteadyToneDoesNotAlternateItsDominantBandAroundTheTemporalBaseline()
        {
            var options = new AnalyzerOptions { SineGain = 1, TransientGain = 1, NoiseGain = 1 };
            var domain = options.Domain;
            var frequencies = domain.ToArray();
            const int bin = 30, rate = 48000, batch = 187;
            var processor = new DFTProcessor(domain, rate, true);
            var splitter = new Splitter(domain, options, rate / (double)batch);
            var input = new double[batch];
            var spectrum = new double[domain.Length];
            var output = new double[4 * domain.Length];
            double minimum = double.PositiveInfinity, maximum = 0, maximumAttack = 0;
            for (var offset = 0; offset < 2 * rate; offset += batch)
            {
                for (var i = 0; i < batch; ++i)
                    input[i] = .5 * Math.Sin(2 * Math.PI * frequencies[bin] * (offset + i) / rate);
                processor.Process(input);
                processor.Output(spectrum);
                splitter.Process(spectrum, output);
                if (offset < rate) continue;
                minimum = Math.Min(minimum, output[4 * bin + 3]);
                maximum = Math.Max(maximum, output[4 * bin + 3]);
                maximumAttack = Math.Max(maximumAttack, output[4 * bin + 1]);
            }
            Assert.That(maximum - minimum, Is.LessThan(.001), "steady dominant-band amplitude ripple");
            Assert.That(maximumAttack, Is.LessThan(.003), "steady tone was classified as repeated attacks");
        }

        [Test]
        public void InaudibleSpectralContrastsDoNotCreateBrightDiagnosticChannels()
        {
            var options = new AnalyzerOptions { SineGain = 1, TransientGain = 1, NoiseGain = 1 };
            var splitter = new Splitter(options.Domain, options);
            var input = Enumerable.Repeat(-10.0, options.Domain.Length).ToArray();
            var output = new double[4 * input.Length];
            for (var i = 0; i < 80; ++i) splitter.Process(input, output);
            input[30] = -8.5; // a large relative change at -170 dBFS
            splitter.Process(input, output);
            Assert.That(output[4 * 30 + 1], Is.Zero);
            for (var i = 0; i < 80; ++i) splitter.Process(input, output);
            Assert.That(output[4 * 30], Is.EqualTo(Math.Pow(10, -8.5)).Within(1e-20));
        }
    }
}

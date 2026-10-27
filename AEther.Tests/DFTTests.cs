using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace AEther.Tests
{
    public class DFTTests
    {
        static IDFTFilter Filter(string kind, double frequency, double[] window) => kind switch
        {
            "rect" => new DFTFilter(frequency, 8, 48000),
            "scalar" => new WindowedDFTFilter(frequency, 8, 48000, window),
            _ => new WindowedDFTFilterSIMD(frequency, 8, 48000, window),
        };

        // Independent direct sum over sample ages, not the recursive update used by the filters.
        static Complex Reference(double[] samples, int count, int length, double[] cosines)
        {
            var bin = (int)Math.Round(1 / (Math.Pow(2, 1.0 / 8) - 1));
            var sum = Complex.Zero;
            for (var age = 0; age < Math.Min(count, length); ++age)
            {
                var weight = cosines[0];
                for (var j = 1; j < cosines.Length; ++j)
                    weight += Math.Pow(-1, j) * cosines[j] * Math.Cos(2 * Math.PI * j * age / length);
                sum += samples[count - 1 - age] * weight * Complex.FromPolarCoordinates(1, 2 * Math.PI * bin * age / length);
            }
            return sum / (length * cosines[0]);
        }

        [TestCase("rect", 440)]
        [TestCase("scalar", 440)]
        [TestCase("simd", 440)]
        [TestCase("scalar", 7000)]
        [TestCase("simd", 7000)]
        public void MatchesDirectWindowedDFTDuringStartupAndRingWrap(string kind, double frequency)
        {
            var cosines = kind == "rect" ? DFTProcessor.RectWindow : DFTProcessor.HannWindow;
            var filter = Filter(kind, frequency, DFTProcessor.CreateWindow(cosines));
            var random = new Random(923);
            var samples = Enumerable.Range(0, 3 * filter.Length + 17).Select(_ => random.NextDouble() - .5).ToArray();
            var processed = 0;
            while (processed < samples.Length)
            {
                var count = Math.Min(73, samples.Length - processed);
                filter.Process(samples.AsMemory(processed, count));
                processed += count;
                Assert.That(Complex.Abs(filter.GetOutput() - Reference(samples, processed, filter.Length, cosines)),
                    Is.LessThan(1e-10), $"{kind}, sample {processed}");
            }
        }

        [TestCase("scalar")]
        [TestCase("simd")]
        public void OnBinSineHasCorrectAmplitudeAndNoPhaseRipple(string kind)
        {
            var filter = Filter(kind, 1000, DFTProcessor.CreateWindow(DFTProcessor.HannWindow));
            var bin = (int)Math.Round(1 / (Math.Pow(2, 1.0 / 8) - 1));
            var samples = Enumerable.Range(0, 4 * filter.Length)
                .Select(i => .7 * Math.Sin(2 * Math.PI * bin * i / filter.Length + .37)).ToArray();
            for (var i = 0; i < samples.Length; ++i)
            {
                filter.Process(samples.AsMemory(i, 1));
                if (i >= filter.Length)
                    Assert.That(2 * filter.GetOutput().Magnitude, Is.EqualTo(.7).Within(1e-10));
            }
        }

        [Test]
        public void SIMDIsAnOptimizationAndDoesNotChangeWindowOrGain([Values] DFTWindow window)
        {
            var domain = new Domain(100, 8, 40);
            var scalar = new DFTProcessor(domain, 48000, false, window);
            var simd = new DFTProcessor(domain, 48000, true, window);
            var random = new Random(23);
            var input = Enumerable.Range(0, 16000).Select(_ => random.NextDouble() - .5).ToArray();
            scalar.Process(input);
            simd.Process(input);
            var expected = new double[domain.Length];
            var actual = new double[domain.Length];
            scalar.Output(expected);
            simd.Output(actual);
            for (var i = 0; i < expected.Length; ++i)
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(1e-9));
        }

        [Test]
        public void EveryWindowHasUnitOnBinGain([Values] DFTWindow window, [Values] bool simd)
        {
            var processor = new DFTProcessor(new Domain(1000, 8, 1), 48000, simd, window);
            var samples = Enumerable.Range(0, 4 * 528)
                .Select(i => .7 * Math.Sin(2 * Math.PI * 1000 * i / 48000 + .37)).ToArray();
            processor.Process(samples);
            var output = new double[1];
            processor.Output(output);
            Assert.That(Math.Pow(10, output[0]), Is.EqualTo(.7).Within(1e-10));
        }

        [Test]
        public void FlatTopReducesHalfBinAmplitudeError()
        {
            double Amplitude(DFTWindow window)
            {
                var filter = new WindowedDFTFilter(1000, 8, 48000,
                    DFTProcessor.CreateWindow(DFTProcessor.GetWindowCosines(window)));
                filter.Process(Enumerable.Range(0, 4 * filter.Length)
                    .Select(i => .7 * Math.Sin(2 * Math.PI * 11.5 * i / filter.Length + .37)).ToArray());
                return 2 * filter.GetOutput().Magnitude;
            }
            Assert.That(Math.Abs(Amplitude(DFTWindow.FlatTop) / .7 - 1), Is.LessThan(.002));
            Assert.That(Amplitude(DFTWindow.Hann), Is.LessThan(.7 * .9));
        }

        [Test]
        public void RejectsUnknownWindow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new DFTProcessor(new Domain(1000, 8, 1), 48000, true, (DFTWindow)999));
        }

        [TestCase("scalar")]
        [TestCase("simd")]
        public void RecurrenceRemainsStableOverAMillionSamplesAndFlushesToSilence(string kind)
        {
            var filter = Filter(kind, 1000, DFTProcessor.CreateWindow(DFTProcessor.HannWindow));
            var bin = (int)Math.Round(1 / (Math.Pow(2, 1.0 / 8) - 1));
            var input = Enumerable.Range(0, 1000000)
                .Select(i => .7 * Math.Sin(2 * Math.PI * bin * i / filter.Length)).ToArray();
            filter.Process(input);
            Assert.That(2 * filter.GetOutput().Magnitude, Is.EqualTo(.7).Within(1e-8));
            filter.Process(new double[2 * filter.Length]);
            Assert.That(filter.GetOutput().Magnitude, Is.LessThan(1e-8));
        }
    }
}

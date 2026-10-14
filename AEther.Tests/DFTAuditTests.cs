#nullable enable
using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace AEther.Tests
{
    public class DFTAuditTests
    {
        static Complex Direct(double[] input, int count, int length, double[] cosines)
        {
            const int bin = 11;
            var result = Complex.Zero;
            for (var age = 0; age < Math.Min(length, count); ++age)
            {
                var weight = cosines[0];
                for (var j = 1; j < cosines.Length; ++j)
                    weight += (j % 2 == 0 ? 1 : -1) * cosines[j] * Math.Cos(2 * Math.PI * j * age / length);
                result += input[count - 1 - age] * weight * Complex.FromPolarCoordinates(1, 2 * Math.PI * bin * age / length);
            }
            return result / (length * cosines[0]);
        }

        [Test]
        public void AuditComplexParityAndDirectReferenceAcrossWindowsFrequenciesAndChunking()
        {
            var windows = new[] { DFTProcessor.RectWindow, DFTProcessor.HannWindow,
                DFTProcessor.HammingWindow, DFTProcessor.BlackmanWindow, DFTProcessor.NuttalWindow };
            var random = new Random(713);
            double maximumParityError = 0, maximumReferenceError = 0;
            var cases = 0;
            foreach (var frequency in new[] { 27.5, 37.9, 441.37, 7040.0, 23000.0 })
            foreach (var cosines in windows)
            {
                var window = DFTProcessor.CreateWindow(cosines);
                var scalar = new WindowedDFTFilter(frequency, 8, 48000, window);
                IDFTFilter? simd = window.Length <= Vector<double>.Count
                    ? new WindowedDFTFilterSIMD(frequency, 8, 48000, window) : null;
                var perSample = new WindowedDFTFilter(frequency, 8, 48000, window);
                var input = Enumerable.Range(0, 2 * scalar.Length + 31).Select(i =>
                    .3 * Math.Sin(i * .173) + .1 + .2 * (random.NextDouble() - .5)).ToArray();
                var checkpoints = new[] { 1, Math.Min(73, scalar.Length - 1), scalar.Length - 1,
                    scalar.Length, scalar.Length + 1, input.Length }.Distinct().OrderBy(i => i);
                var offset = 0;
                foreach (var count in checkpoints)
                {
                    while (offset < count)
                    {
                        var chunk = Math.Min(137, count - offset);
                        scalar.Process(input.AsMemory(offset, chunk));
                        simd?.Process(input.AsMemory(offset, chunk));
                        for (var i = 0; i < chunk; ++i) perSample.Process(input[offset + i]);
                        offset += chunk;
                    }
                    var expected = Direct(input, count, scalar.Length, cosines);
                    maximumReferenceError = Math.Max(maximumReferenceError, Complex.Abs(scalar.GetOutput() - expected));
                    Assert.That(Complex.Abs(scalar.GetOutput() - expected), Is.LessThan(1e-10));
                    Assert.That(Complex.Abs(perSample.GetOutput() - scalar.GetOutput()), Is.LessThan(1e-12));
                    if (simd is not null)
                    {
                        maximumParityError = Math.Max(maximumParityError, Complex.Abs(simd.GetOutput() - scalar.GetOutput()));
                        Assert.That(Complex.Abs(simd.GetOutput() - scalar.GetOutput()), Is.LessThan(1e-10));
                    }
                }
                cases++;
            }
            TestContext.Out.WriteLine($"Vector<double>.Count={Vector<double>.Count}; hardware acceleration={Vector.IsHardwareAccelerated}; cases={cases}; max scalar/SIMD complex error={maximumParityError:E6}; max direct-reference error={maximumReferenceError:E6}");
        }

        [Test]
        public void AuditDefaultFrequencyQuantization()
        {
            var domain = new AnalyzerOptions().Domain;
            var worst = domain.Select(f =>
            {
                var n = (int)Math.Round(11 * 48000 / f);
                return (requested: f, actual: 11.0 * 48000 / n, length: n);
            }).OrderByDescending(p => Math.Abs(p.actual / p.requested - 1)).First();
            TestContext.Out.WriteLine($"Default band count={domain.Length}; largest tuning error: requested={worst.requested:F6} Hz actual={worst.actual:F6} Hz N={worst.length}, relative={100 * Math.Abs(worst.actual / worst.requested - 1):F6}%");
            Assert.That(Math.Abs(worst.actual / worst.requested - 1), Is.LessThan(.01));
        }

        [Test]
        public void AuditResidualAfterLongConstantInput()
        {
            var window = DFTProcessor.CreateWindow(DFTProcessor.HannWindow);
            double worstResidual = 0;
            foreach (var frequency in new[] { 27.5, 441.37, 7040.0 })
            {
                var scalar = new WindowedDFTFilter(frequency, 8, 48000, window);
                var ones = Enumerable.Repeat(.5, 1000).ToArray();
                for (var i = 0; i < 10000; ++i) scalar.Process(ones); // 10 million samples, 208 seconds
                scalar.Process(new double[2 * scalar.Length]);
                var residual = scalar.GetOutput().Magnitude;
                worstResidual = Math.Max(worstResidual, residual);
                Assert.That(residual, Is.LessThan(1e-8));
            }
            TestContext.Out.WriteLine($"10M-sample DC then silence: largest normalized residual={worstResidual:E6}");
        }
    }
}

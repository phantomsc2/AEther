using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace AEther.Tests;

public class DFTSilenceTests
{
    [Test]
    public void ScalarAndSimdFiltersClearOnlyWhenTheirActualSampleRingsAreEmpty()
    {
        foreach (var cosines in Enum.GetValues<DFTWindow>().Select(DFTProcessor.GetWindowCosines)
            .Append(Enumerable.Repeat(.1, 20).ToArray())) // exercises SIMD's general group path
        {
            var window = DFTProcessor.CreateWindow(cosines);
            foreach (IDFTFilter filter in new IDFTFilter[] {
                new DFTFilter(500, 4, 8000), new WindowedDFTFilter(500, 4, 8000, window),
                new WindowedDFTFilterSIMD(500, 4, 8000, window) })
            foreach (bool chunks in new[] { false, true })
            {
                var active = Enumerable.Range(0, 3 * filter.Length).Select(j => .5 * Math.Sin(j * .37)).ToArray();
                Feed(active);
                Feed(new double[filter.Length]);
                Assert.That(filter.GetOutput(), Is.EqualTo(Complex.Zero));
                // A quiet sample immediately after exact expiry must start from
                // zero state; never estimate silence using an amplitude threshold.
                Feed(new[] { 1e-180 });
                Feed(new double[1]); // Move off the Hann window's exactly zero endpoint.
                Assert.That(filter.GetOutput().Magnitude, Is.GreaterThan(0));
                Feed(new double[filter.Length]);
                Assert.That(filter.GetOutput(), Is.EqualTo(Complex.Zero));
                // Empty and nonempty portions can occur in the same bulk call.
                var restart = new double[filter.Length + 7];
                restart[0] = .2; restart[^1] = 1e-180;
                Feed(restart);
                Feed(new double[1]);
                Assert.That(filter.GetOutput().Magnitude, Is.LessThan(1e-179));

                void Feed(double[] samples)
                {
                    if (chunks) filter.Process(samples);
                    else foreach (var sample in samples) filter.Process(sample);
                }
            }
        }
    }

    [TestCase(false), TestCase(true)]
    public void ProcessorPublishesTrueLogZeroWithoutFlooringQuietNonzeroInput(bool simd)
    {
        var domain = new Domain(500, 4, 2);
        var processor = new DFTProcessor(domain, 8000, simd);
        var log = new double[2]; var linear = new double[2];
        processor.Output(log); processor.OutputLinear(linear);
        Assert.That(log, Is.All.EqualTo(double.NegativeInfinity));
        Assert.That(linear, Is.All.Zero);
        processor.Process(Enumerable.Repeat(1e-100, 20).ToArray());
        processor.Output(log); processor.OutputLinear(linear);
        Assert.That(linear, Is.All.GreaterThan(0));
        Assert.That(log, Is.All.LessThan(-90));
    }
}

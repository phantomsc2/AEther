using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace AEther.Tests;

public class SwiftTests
{
    static double Weight(int k, SwiftBand band, double fs)
    {
        if (k == 0) return 0;
        if (band.SlowSeconds == 0) return k == 1 ? 1 : 0;
        var ls = -1 / (fs * band.SlowSeconds);
        var lf = -1 / (fs * band.FastSeconds);
        // sinh form remains accurate for nearly equal poles even on runtimes
        // whose ExpM1 implementation subtracts 1 after evaluating exp.
        static double Em1(double x) => 2 * Math.Exp(x / 2) * Math.Sinh(x / 2);
        if (band.FastSeconds == 0) return -Em1(ls) * Math.Exp((k - 1) * ls);
        var ca = -Em1(ls); var cb = -Em1(lf);
        return ca * cb * Math.Exp((k - 1) * ls) * (ls == lf
            ? k : Em1(k * (lf - ls)) / Em1(lf - ls));
    }

    [TestCase(.25, 0), TestCase(1, 0), TestCase(8, 0)]
    [TestCase(1, .25), TestCase(1, 1e-10), TestCase(1, 1)]
    public void ScalarAndSimdMatchIndependentDirectWindow(double width, double imbalance)
    {
        const int fs = 8000;
        var domain = new Domain(37.9, 12, 13);
        var settings = domain.Select(f => SwiftTuning.FromBandwidth(f, domain.Resolution, fs, width, imbalance)).ToArray();
        var scalar = new SwiftProcessor(settings, fs, false);
        var simd = new SwiftProcessor(settings, fs, true);
        var random = new Random(713);
        var samples = Enumerable.Range(0, 1800).Select(i => .1 + .3 * Math.Sin(i * .173) + .1 * random.NextDouble()).ToArray();
        int start = 0;
        foreach (var end in new[] { 1, 7, 137, 613, 1800 })
        {
            scalar.Process(samples.AsMemory(start, end - start));
            for (var i = start; i < end; ++i) simd.Process(samples.AsMemory(i, 1));
            for (var k = 0; k < settings.Length; ++k)
            {
                Complex expected = Complex.Zero;
                for (var age = 0; age < end; ++age)
                    expected += samples[end - 1 - age] * Weight(age, settings[k], fs)
                        * Complex.FromPolarCoordinates(1, 2 * Math.PI * settings[k].Frequency * age / fs);
                Assert.That(Complex.Abs(scalar.GetOutput(k) - expected), Is.LessThan(2e-12));
                Assert.That(Complex.Abs(simd.GetOutput(k) - expected), Is.LessThan(2e-12));
            }
            start = end;
        }
        scalar.Reset(); simd.Reset();
        for (var k = 0; k < settings.Length; ++k)
            Assert.That(scalar.GetOutput(k), Is.EqualTo(Complex.Zero));
        scalar.Process(samples); simd.Process(samples);
        for (var k = 0; k < settings.Length; ++k)
            Assert.That(Complex.Abs(scalar.GetOutput(k) - simd.GetOutput(k)), Is.LessThan(2e-12));
    }

    [Test]
    public void CalibrationMatchesGeometricBinBandwidth()
    {
        foreach (var fs in new[] { 8000, 44100, 48000 })
        foreach (var resolution in new[] { 1, 6, 12, 24, 48, 96 })
        foreach (var f in new[] { 30.0, 55.0, 441.37, .49 * fs })
        foreach (var width in new[] { .25, 1.0, 2.0, 8.0 })
        foreach (var d in new[] { 0.0, .25, .9, .999999999, 1.0 })
        {
            // Use the two boundaries rather than production's equivalent sinh expression.
            var bandwidth = width * (f * Math.Pow(2, .5 / resolution) - f * Math.Pow(2, -.5 / resolution));
            if (bandwidth >= fs)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => SwiftTuning.FromBandwidth(f, resolution, fs, width, d));
                continue;
            }
            var band = SwiftTuning.FromBandwidth(f, resolution, fs, width, d);
            var sum = band.SlowSeconds + band.FastSeconds;
            Assert.That((band.SlowSeconds - band.FastSeconds) / sum, Is.EqualTo(d).Within(2e-15));
            if (d == 0) Assert.That(band.SlowSeconds, Is.EqualTo(band.FastSeconds));
            if (d == 1) Assert.That(band.FastSeconds, Is.Zero);
            Assert.That(SwiftTuning.Response(0, fs, band.SlowSeconds, band.FastSeconds).Magnitude,
                Is.EqualTo(1).Within(1e-13));
            foreach (var sign in new[] { -1, 1 })
            {
                var offset = sign * bandwidth / 2;
                var z = Complex.FromPolarCoordinates(1, -2 * Math.PI * offset / fs);
                Complex Stage(double time)
                {
                    var pole = time == 0 ? 0 : Math.Exp(-1 / (fs * time));
                    return (1 - pole) / (Complex.One - pole * z);
                }
                var independent = (Stage(band.SlowSeconds) * Stage(band.FastSeconds)).Magnitude;
                Assert.That(independent * independent, Is.EqualTo(.5).Within(1e-9),
                    $"fs={fs}, R={resolution}, f={f}, width={width}, d={d}");
            }
        }
    }

    [TestCase(55.0, 0), TestCase(441.37, .25), TestCase(7000.0, 1), TestCase(23500.0, 0)]
    public void RealToneGainAndImageAgreeWithAnalyticResponse(double f, double d)
    {
        const double fs = 48000, amplitude = .7, phase = .37;
        var settings = new[] { SwiftTuning.FromBandwidth(f, 12, fs, imbalance: d) };
        var processor = new SwiftProcessor(settings, fs, true);
        var count = (int)Math.Ceiling(42 * settings[0].SlowSeconds * fs) + 200;
        var tone = f * Math.Pow(2, .3 / 12);
        var samples = Enumerable.Range(0, count).Select(i => amplitude * Math.Cos(2 * Math.PI * tone * i / fs + phase)).ToArray();
        processor.Process(samples);
        var angle = 2 * Math.PI * tone * (count - 1) / fs + phase;
        var expected = amplitude / 2 * (SwiftTuning.Response(tone - f, fs, settings[0].SlowSeconds, settings[0].FastSeconds)
            * Complex.FromPolarCoordinates(1, angle) + SwiftTuning.Response(-tone - f, fs, settings[0].SlowSeconds, settings[0].FastSeconds)
            * Complex.FromPolarCoordinates(1, -angle));
        Assert.That(Complex.Abs(processor.GetOutput(0) - expected), Is.LessThan(2e-11));
    }

    [TestCase(false, .25, 0), TestCase(true, .25, 0)]
    [TestCase(false, 1, .5), TestCase(true, 1, .5)]
    [TestCase(false, 8, 1), TestCase(true, 8, 1)]
    public void SilenceExpiresBySampleCountAndRetainsQuietInput(bool simd, double width, double d)
    {
        const double fs = 8000;
        var domain = new Domain(500, 12, 7);
        var a = new SwiftProcessor(domain, fs, simd, width, d);
        var b = new SwiftProcessor(domain, fs, simd, width, d);
        var active = Enumerable.Range(0, 2000).Select(i => .3 * Math.Sin(i * .37)).ToArray();
        a.Process(active); b.Process(active);
        int expiry = (int)a.Bands.ToArray().Max(x => x.SilenceSamples);
        var zeros = new double[expiry];
        a.Process(zeros);
        for (int i = 0; i < zeros.Length; i += 73) b.Process(zeros.AsMemory(i, Math.Min(73, zeros.Length - i)));
        for (int k = 0; k < domain.Length; ++k)
        {
            Assert.That(a.GetOutput(k), Is.EqualTo(Complex.Zero));
            Assert.That(b.GetOutput(k), Is.EqualTo(Complex.Zero));
        }
        var linear = new double[domain.Length]; var log = new double[domain.Length];
        a.OutputLinear(linear); a.Output(log);
        Assert.That(linear, Is.All.Zero); Assert.That(log, Is.All.EqualTo(double.NegativeInfinity));
        a.Process(new[] { 1e-180, 0.0 }); a.OutputLinear(linear);
        Assert.That(linear, Is.All.GreaterThan(0));
        // A last nonzero inside a large zero block restarts the silence clock.
        var restart = new double[expiry + 3]; restart[^2] = 1e-180;
        a.Process(restart); a.OutputLinear(linear);
        Assert.That(linear, Is.All.GreaterThan(0));
        a.Reset(); a.OutputLinear(linear); Assert.That(linear, Is.All.Zero);
    }

    [Test]
    public void ToggleRoutesToBackendAndProcessingDoesNotAllocate()
    {
        var options = new AnalyzerOptions();
        Assert.That(options.SwiftWidthScale, Is.EqualTo(1));
        Assert.That(options.SwiftImbalance, Is.Zero);
        Assert.That(SpectrumProcessor.Create(options.Domain, 48000, options), Is.TypeOf<SwiftProcessor>());
        options.UseSwift = false;
        Assert.That(SpectrumProcessor.Create(options.Domain, 48000, options), Is.TypeOf<DFTProcessor>());
        foreach (var simd in new[] { false, true })
        foreach (var d in new[] { 0.0, .5, 1.0 })
        {
            var processor = new SwiftProcessor(options.Domain, 48000, simd, imbalance: d);
            var input = Enumerable.Range(0, 187).Select(i => .1 * Math.Sin(i * .3)).ToArray();
            var output = new double[options.Domain.Length];
            for (var i = 0; i < 100; ++i) { processor.Process(input); processor.OutputLinear(output); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; ++i) { processor.Process(input); processor.OutputLinear(output); }
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        }
    }

    [Test]
    public void ZeroDecayLimitRetainsTheOneSampleDelay()
    {
        foreach (var simd in new[] { false, true })
        {
            var processor = new SwiftProcessor(new[] { new SwiftBand(3900, 0, 0, 2) }, 8000, simd);
            Assert.That(processor.Bands.Span[0].SlowSeconds, Is.Zero);
            processor.Process(new[] { .25 });
            Assert.That(processor.GetOutput(0), Is.EqualTo(Complex.Zero));
            processor.Process(new[] { 0.0 });
            var expected = .25 * Complex.FromPolarCoordinates(1, 2 * Math.PI * 3900 / 8000);
            Assert.That(Complex.Abs(processor.GetOutput(0) - expected), Is.LessThan(1e-14));
            processor.Process(new[] { 0.0 });
            Assert.That(processor.GetOutput(0), Is.EqualTo(Complex.Zero));
        }
    }

    [Test]
    public void InvalidTuningIsRejected()
    {
        var format = new SampleFormat(SampleType.Float32.Instance, 48000, 2);
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -.01, 1.01 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new AnalyzerOptions { SwiftImbalance = value }.Validate(format));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, .24, 8.01 })
            Assert.Throws<ArgumentOutOfRangeException>(() => new AnalyzerOptions { SwiftWidthScale = value }.Validate(format));
        var coarse = new AnalyzerOptions { MinFrequency = 6000, MaxFrequency = 24000, FrequencyResolution = 1, SwiftWidthScale = 8 };
        Assert.Throws<ArgumentOutOfRangeException>(() => coarse.Validate(format));
        coarse.UseSwift = false;
        coarse.Validate(format);
        Assert.Throws<ArgumentOutOfRangeException>(() => SwiftTuning.FromBandwidth(440, 12, 48000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SwiftTuning.FromBandwidth(440, 12, 48000, imbalance: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => SwiftTuning.FromBandwidth(440, 0, 48000));
        Assert.Throws<ArgumentOutOfRangeException>(() => SwiftTuning.FromBandwidth(25000, 12, 48000));
    }

    [TestCase(0.0), TestCase(.5), TestCase(1.0)]
    public void BackendFactoryAppliesWidthAndImbalance(double d)
    {
        var options = new AnalyzerOptions { SwiftWidthScale = 2, SwiftImbalance = d };
        var processor = (SwiftProcessor)SpectrumProcessor.Create(options.Domain, 48000, options);
        foreach (var band in processor.Bands.Span)
        {
            var expected = SwiftTuning.FromBandwidth(band.Frequency, options.FrequencyResolution, 48000, 2, d);
            Assert.That(band, Is.EqualTo(expected));
        }
    }

    [Test]
    public void EqualStageSolveMatchesClosedFormAndNotebook()
    {
        const double f = 480, fs = 48000;
        var width = f * (Math.Pow(2, 1.0 / 24) - Math.Pow(2, -1.0 / 24));
        var sum = 1 / (fs * Math.Asinh(Math.Sin(Math.PI * width / (2 * fs)) / Math.Sqrt(Math.Sqrt(2) - 1)));
        var band = SwiftTuning.FromBandwidth(f, 12, fs);
        Assert.That(band.SlowSeconds + band.FastSeconds, Is.EqualTo(sum).Within(1e-14));
        Assert.That(sum * 1000, Is.EqualTo(14.776).Within(.001));
        var broader = SwiftTuning.FromBandwidth(f, 12, fs, widthScale: 2);
        Assert.That(broader.SlowSeconds + broader.FastSeconds, Is.LessThan(sum));
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public class GlobalEvidenceTests
{
    static AnalyzerOptions Options(bool time = false, bool frequency = false) => new()
    {
        TimeResolution = 100, TimeWindowSeconds = .08, FrequencyWindowOctaves = 2,
        JointTimeSlopeEnabled = time, JointFrequencySlopeEnabled = frequency,
    };

    [Test]
    public void GlobalAndClippedLocalFitsMatchDenseProjectionThroughStartupAndWrap()
    {
        var random = new Random(713);
        foreach (bool time in new[] { false, true })
        foreach (bool frequency in new[] { false, true })
        for (int bands = 1; bands <= 7; bands++)
        {
            var options = Options(time, frequency);
            var domain = new Domain(100, 1, bands);
            var global = new GlobalSplitter(domain, options);
            var local = new Splitter(domain, options);
            var output = new double[4 * bands];
            var history = new List<double[]>();
            for (int row = 0; row < 35; row++)
            {
                // Includes random interaction and an exact bilinear patch after ring wrap.
                var input = Enumerable.Range(0, bands).Select(f => row < 18
                    ? random.NextDouble() : 1 + .02 * row + .03 * f + .001 * row * f).ToArray();
                history.Add(input);
                if (history.Count > 9) history.RemoveAt(0);
                var actual = global.Process(input, 1);
                local.ProcessLinear(input, output);
                double ss = 0;
                for (int f = 0; f < bands; f++)
                {
                    var expected = Oracle(history, 0, bands, f, time, frequency);
                    Check(global.Estimates.Span[f], expected);
                    ss += expected.Sine * expected.Sine;
                    Check(local.Estimates.Span[f], Oracle(history, Math.Max(0, f - 1),
                        Math.Min(bands, f + 2), f, time, frequency));
                }
                var b = history.SelectMany(x => x).Average();
                Assert.That(actual.Background, Is.EqualTo(b).Within(2e-12));
                Assert.That(actual.Transient, Is.EqualTo(input.Average() - b).Within(2e-12));
                Assert.That(actual.CurrentMean, Is.EqualTo(input.Average()).Within(2e-12));
                Assert.That(actual.Structure, Is.EqualTo(Math.Sqrt(ss / bands)).Within(2e-12));
                Assert.That(actual.InteractionRms, Is.EqualTo(global.Estimates.Span[0].ResidualRms));
                Assert.That(actual.ResidualDegreesOfFreedom, Is.EqualTo(
                    (bands - (frequency && bands > 1 ? 2 : 1)) * (history.Count - (time && history.Count > 1 ? 2 : 1))));
            }
        }
    }

    // Independent dense QR projection of the complete model space. Direct
    // component ownership is evaluated separately, without rolling moments.
    static JointEvidence Oracle(List<double[]> rows, int first, int end, int bin, bool time, bool frequency)
    {
        int fCount = end - first, n = rows.Count, size = fCount * n;
        var y = Enumerable.Range(0, size).Select(i => rows[i / fCount][first + i % fCount]).ToArray();
        double X(int f) => frequency && fCount > 1 ? 2.0 * f / (fCount - 1) - 1 : 0;
        double U(int j) => time && n > 1 ? 2.0 * j / (n - 1) - 1 : 0;
        var basis = new List<double[]>();
        void Add(Func<int, int, double> value)
        {
            var v = Enumerable.Range(0, size).Select(i => value(i % fCount, i / fCount)).ToArray();
            for (int pass = 0; pass < 2; pass++)
                foreach (var q in basis)
                {
                    var dot = v.Zip(q, (a, b) => a * b).Sum();
                    for (int i = 0; i < size; i++) v[i] -= dot * q[i];
                }
            var length = Math.Sqrt(v.Sum(x => x * x));
            if (length < 1e-10) return;
            basis.Add(v.Select(x => x / length).ToArray());
        }
        for (int f = 0; f < fCount; f++)
        {
            int target = f;
            Add((k, j) => k == target ? 1 : 0);
            if (time) Add((k, j) => k == target ? U(j) : 0);
        }
        for (int j = 0; j < n; j++)
        {
            int target = j;
            Add((k, t) => t == target ? 1 : 0);
            if (frequency) Add((k, t) => t == target ? X(k) : 0);
        }
        var fitted = new double[size];
        foreach (var q in basis)
        {
            var dot = y.Zip(q, (a, b) => a * b).Sum();
            for (int i = 0; i < size; i++) fitted[i] += dot * q[i];
        }
        double b0 = y.Average(), vx = Enumerable.Range(0, fCount).Average(f => X(f) * X(f)),
            vu = Enumerable.Range(0, n).Average(j => U(j) * U(j));
        var m = Enumerable.Range(0, fCount).Select(f => rows.Average(r => r[first + f])).ToArray();
        var q0 = rows.Select(r => r.Skip(first).Take(fCount).Average()).ToArray();
        var a0 = Enumerable.Range(0, fCount).Select(f => vu == 0 ? 0 :
            Enumerable.Range(0, n).Average(j => rows[j][first + f] * U(j)) / vu).ToArray();
        var d = rows.Select(r => vx == 0 ? 0 :
            Enumerable.Range(0, fCount).Average(f => r[first + f] * X(f)) / vx).ToArray();
        double c = vx == 0 ? 0 : Enumerable.Range(0, fCount).Average(f => a0[f] * X(f)) / vx;
        int k0 = bin - first;
        double s = m[k0] - b0 + (a0[k0] - a0.Average() - .5 * c * X(k0)) * U(n - 1);
        double t0 = q0[^1] - b0 + (d[^1] - d.Average() - .5 * c * U(n - 1)) * X(k0);
        double r0 = Math.Sqrt(y.Zip(fitted, (a, b) => (a - b) * (a - b)).Average());
        Assert.That(b0 + s + t0, Is.EqualTo(fitted[(n - 1) * fCount + k0]).Within(2e-12));
        Assert.That(size - basis.Count, Is.EqualTo((fCount - (frequency && fCount > 1 ? 2 : 1))
            * (n - (time && n > 1 ? 2 : 1))));
        return new(s, t0, b0, r0, rows[^1][bin], fCount, n);
    }

    static void Check(JointEvidence actual, JointEvidence expected)
    {
        Assert.That(actual.Sine, Is.EqualTo(expected.Sine).Within(2e-12));
        Assert.That(actual.Transient, Is.EqualTo(expected.Transient).Within(2e-12));
        Assert.That(actual.Background, Is.EqualTo(expected.Background).Within(2e-12));
        Assert.That(actual.ResidualRms, Is.EqualTo(expected.ResidualRms).Within(2e-10));
        Assert.That(actual.FrequencySamples, Is.EqualTo(expected.FrequencySamples));
        Assert.That(actual.TimeSamples, Is.EqualTo(expected.TimeSamples));
    }

    [Test]
    public void IdealizedLevelsAndCoherentSwellHaveDistinctMeanStructureAndInteraction()
    {
        var domain = new Domain(100, 1, 3);
        var fit = new GlobalSplitter(domain, Options());
        GlobalEvidence e = default;
        for (int j = 0; j < 9; j++) e = fit.Process(new[] { 0.0, 3, 0 }, 1);
        Assert.That(e.Background, Is.EqualTo(1).Within(1e-14));
        Assert.That(e.Structure, Is.EqualTo(Math.Sqrt(2)).Within(1e-14));
        Assert.That(e.Transient, Is.Zero);
        Assert.That(e.InteractionRms, Is.Zero);
        fit.Reset();
        for (int j = 0; j < 9; j++) e = fit.Process(new[] { 3.0 * j, 0, 0 }, 1);
        Assert.That(e.Background, Is.EqualTo(4).Within(1e-14));
        Assert.That(e.Structure, Is.EqualTo(4 * Math.Sqrt(2)).Within(1e-14));
        Assert.That(e.InteractionRms, Is.EqualTo(Math.Sqrt(2) * Math.Sqrt(60.0 / 9)).Within(1e-14),
            "A coherent narrow-note swell has interaction; it is not classified as noise.");
        fit.Reset();
        for (int j = 0; j < 8; j++) fit.Process(new double[3], 1);
        e = fit.Process(new[] { 3.0, 3, 3 }, 1);
        Assert.That(e.Background, Is.EqualTo(1.0 / 3).Within(1e-14));
        Assert.That(e.Transient, Is.EqualTo(8.0 / 3).Within(1e-14));
        Assert.That(e.Structure, Is.Zero);
        Assert.That(e.InteractionRms, Is.Zero);
        for (int j = 0; j < 9; j++) e = fit.Process(new double[3], 1);
        Assert.That(e.Background, Is.Zero);
        Assert.That(e.Structure, Is.Zero);
        Assert.That(e.Transient, Is.Zero);
        Assert.That(e.InteractionRms, Is.Zero);
    }

    [Test]
    public void ChannelAveragingPrecedesTheFitAndDoesNotAverageLocalBackgroundsOrResiduals()
    {
        var domain = new Domain(100, 1, 3);
        var mono = new GlobalSplitter(domain, Options());
        var stereo = new GlobalSplitter(domain, Options());
        var gained = new GlobalSplitter(domain, Options());
        var local = new Splitter(domain, Options());
        var localOutput = new double[12];
        for (int row = 0; row < 40; row++)
        {
            var a = new[] { (double)(row % 4), .2, .8 };
            var b = new[] { 4.0 - row % 4, .2, .8 };
            var expected = mono.Process(a.Zip(b, (x, y) => (x + y) / 2).ToArray(), 1);
            var actual = stereo.Process(a.Concat(b).ToArray(), 2);
            Assert.That(actual, Is.EqualTo(expected));
            var gain = gained.Process(a.Concat(b).Select(x => 1000 * x).ToArray(), 2);
            Assert.That(gain.Background, Is.EqualTo(1000 * actual.Background).Within(1e-10));
            Assert.That(gain.Structure, Is.EqualTo(1000 * actual.Structure).Within(1e-10));
            Assert.That(gain.InteractionRms, Is.Zero);
            local.ProcessLinear(a, localOutput);
            if (row == 39)
                Assert.That(local.Estimates.ToArray().Average(x => x.Background),
                    Is.Not.EqualTo(actual.Background).Within(1e-3));
        }
        var single = new GlobalSplitter(domain, Options());
        var duplicate = new GlobalSplitter(domain, Options());
        for (int j = 0; j < 30; j++)
        {
            var a = new[] { .2 * j, .3, .7 };
            Assert.That(duplicate.Process(a.Concat(a).ToArray(), 2), Is.EqualTo(single.Process(a, 1)));
        }
    }

    [Test]
    public void ExtremeFiniteMeansAndRmsDoNotOverflowAndQuietInputSurvives()
    {
        foreach (double scale in new[] { 1e-250, 1.0, 1e300, double.MaxValue })
        {
            var fit = new GlobalSplitter(new Domain(100, 1, 3), Options());
            for (int j = 0; j < 50; j++)
            {
                var e = fit.Process(new[] { scale, scale / 2, 0, scale, scale / 2, 0 }, 2);
                Assert.That(e.Background / scale, Is.EqualTo(.5).Within(1e-13));
                Assert.That(e.Structure / scale, Is.EqualTo(Math.Sqrt(1.0 / 6)).Within(1e-13));
                Assert.That(e.Transient, Is.Zero);
                Assert.That(e.InteractionRms, Is.Zero);
            }
        }
        var varying = new GlobalSplitter(new Domain(100, 1, 3), Options(true, true));
        foreach (double scale in new[] { 1e-200, 1, 1e300, 1e-200 })
            for (int j = 0; j < 30; j++)
            {
                var e = varying.Process(new[] { scale, .3 * scale, .2 * scale }, 1);
                if (j < 20) continue;
                Assert.That(e.Background / scale, Is.EqualTo(.5).Within(1e-12));
                Assert.That(e.Structure / scale, Is.GreaterThan(0));
            }
    }

    [TestCase(false, 0), TestCase(true, 0), TestCase(false, 60), TestCase(true, 60)]
    public async Task WaveformPathPreservesOppositePhaseStereoAndExpiresToExactZero(bool simd, double gainDb)
    {
        var options = Options();
        options.MinFrequency = 100; options.MaxFrequency = 1000;
        options.FrequencyResolution = 4; options.SIMDEnabled = simd; options.InputGainDb = gainDb;
        var analyzer = new Analyzer(new(SampleType.Float32.Instance, 8000, 2), options);
        var globals = new List<GlobalEvidence>();
        GlobalEvidence joint = default;
        analyzer.JointEvidenceAvailable += (_, e) => joint = e.GlobalEvidence;
        analyzer.SamplesAnalyzed += (_, e) =>
        {
            Assert.That(e.GlobalEvidence, Is.EqualTo(joint));
            Assert.That(e.GlobalEvidence.CurrentMean, Is.EqualTo(Enumerable.Range(0, options.Domain.Length)
                .Average(k => e.Samples[4 * k + 3])).Within(1e-10));
            Assert.That(e.GetChannel(0).ToArray(), Is.EqualTo(e.GetChannel(1).ToArray()));
            globals.Add(e.GlobalEvidence);
            if (globals.Count > 100)
            {
                Assert.That(e.Samples.Take(e.SampleCount * 2), Is.All.Zero);
                Assert.That(e.GlobalEvidence.CurrentMean, Is.Zero);
                Assert.That(e.GlobalEvidence.Structure, Is.Zero);
                Assert.That(e.GlobalEvidence.InteractionRms, Is.Zero);
            }
        };
        var samples = new float[2 * 16000];
        for (int j = 0; j < 2000; j++)
        {
            samples[2 * j] = (float)(.3 * Math.Sin(j * .17));
            samples[2 * j + 1] = -samples[2 * j];
        }
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        analyzer.PostSamples(bytes); analyzer.Stop();
        await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(globals.Count, Is.EqualTo(200));
        Assert.That(globals.Max(x => x.Background), Is.GreaterThan(.001));
    }

    [Test]
    public void LongStreamingRebasesMatchRetainedRawMeansAcrossRangeChanges()
    {
        var fit = new GlobalSplitter(new Domain(100, 1, 7), Options(true, true));
        var random = new Random(975);
        var history = new List<double[]>();
        for (int row = 0; row < 20000; row++)
        {
            double scale = row / 3000 % 2 == 0 ? 1e-100 : 1e100;
            var input = Enumerable.Range(0, 7).Select(_ => scale * random.NextDouble()).ToArray();
            history.Add(input);
            if (history.Count > 9) history.RemoveAt(0);
            var actual = fit.Process(input, 1);
            if (row % 137 != 0) continue;
            var maximum = history.SelectMany(x => x).Max();
            var normalized = history.Select(r => r.Select(x => x / maximum).ToArray()).ToList();
            var expected = Oracle(normalized, 0, 7, 0, true, true);
            Assert.That(actual.Background / maximum, Is.EqualTo(expected.Background).Within(2e-12));
            Assert.That(actual.InteractionRms / maximum, Is.EqualTo(expected.ResidualRms).Within(2e-10));
        }
    }

    [Test, Explicit("Manual streaming latency/allocation benchmark")]
    public void ProfileDefaultGlobalFit()
    {
        var options = new AnalyzerOptions();
        var fit = new GlobalSplitter(options.Domain, options);
        var input = Enumerable.Range(0, options.Domain.Length * 2).Select(i => .1 + .01 * (i % 13)).ToArray();
        for (int j = 0; j < 2000; j++) fit.Process(input, 2);
        var timer = new Stopwatch();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        timer.Start();
        for (int j = 0; j < 20000; j++) fit.Process(input, 2);
        timer.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        TestContext.Out.WriteLine($"Global fit: {options.Domain.Length} bins, 201 rows: {timer.Elapsed.TotalMicroseconds / 20000:F2} us/row; {allocated} bytes.");
    }
}

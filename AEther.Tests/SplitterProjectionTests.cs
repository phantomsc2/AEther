using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace AEther.Tests;

public class SplitterProjectionTests
{
    static AnalyzerOptions Options() => new()
    {
        TimeResolution = 100, TimeWindowSeconds = .08, FrequencyWindowOctaves = 2,
    };

    [Test]
    public void ClippedLocalFitsMatchDenseProjectionThroughStartupAndWrap()
    {
        var random = new Random(713);
        for (int bands = 1; bands <= 7; bands++)
        {
            var local = new Splitter(new Domain(100, 1, bands), Options());
            var output = new double[4 * bands];
            var history = new List<double[]>();
            for (int row = 0; row < 35; row++)
            {
                // Includes random data and bilinear interaction after ring wrap.
                var input = Enumerable.Range(0, bands).Select(f => row < 18
                    ? random.NextDouble() : 1 + .02 * row + .03 * f + .001 * row * f).ToArray();
                history.Add(input);
                if (history.Count > 9) history.RemoveAt(0);
                local.ProcessLinear(input, output);
                for (int f = 0; f < bands; f++)
                    Check(local.Estimates.Span[f], Oracle(history, Math.Max(0, f - 1),
                        Math.Min(bands, f + 2), f));
            }
        }
    }

    // Independent dense QR projection of the complete model space. Direct
    // component ownership is evaluated separately, without rolling moments.
    static JointEvidence Oracle(List<double[]> rows, int first, int end, int bin)
    {
        int fCount = end - first, n = rows.Count, size = fCount * n;
        var y = Enumerable.Range(0, size).Select(i => rows[i / fCount][first + i % fCount]).ToArray();
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
        }
        for (int j = 0; j < n; j++)
        {
            int target = j;
            Add((k, t) => t == target ? 1 : 0);
        }
        var fitted = new double[size];
        foreach (var q in basis)
        {
            var dot = y.Zip(q, (a, b) => a * b).Sum();
            for (int i = 0; i < size; i++) fitted[i] += dot * q[i];
        }
        double b0 = y.Average();
        var m = Enumerable.Range(0, fCount).Select(f => rows.Average(r => r[first + f])).ToArray();
        var q0 = rows.Select(r => r.Skip(first).Take(fCount).Average()).ToArray();
        int k0 = bin - first;
        double s = m[k0] - b0;
        double t0 = q0[^1] - b0;
        double r0 = Math.Sqrt(y.Zip(fitted, (a, b) => (a - b) * (a - b)).Average());
        Assert.That(b0 + s + t0, Is.EqualTo(fitted[(n - 1) * fCount + k0]).Within(2e-12));
        Assert.That(size - basis.Count, Is.EqualTo((fCount - 1) * (n - 1)));
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
        Assert.That(actual.ResidualDegreesOfFreedom, Is.EqualTo(expected.ResidualDegreesOfFreedom));
    }

    [Test]
    public void LongStreamingRebasesMatchRetainedRawMeansAcrossRangeChanges()
    {
        var fit = new Splitter(new Domain(100, 1, 7), Options());
        var output = new double[4 * 7];
        var random = new Random(975);
        var history = new List<double[]>();
        for (int row = 0; row < 20000; row++)
        {
            double scale = row / 3000 % 2 == 0 ? 1e-100 : 1e100;
            var input = Enumerable.Range(0, 7).Select(_ => scale * random.NextDouble()).ToArray();
            history.Add(input);
            if (history.Count > 9) history.RemoveAt(0);
            fit.ProcessLinear(input, output);
            var actual = fit.Estimates.Span[3];
            if (row % 137 != 0) continue;
            var maximum = history.SelectMany(x => x).Max();
            var normalized = history.Select(r => r.Select(x => x / maximum).ToArray()).ToList();
            var expected = Oracle(normalized, 2, 5, 3);
            Assert.That(actual.Background / maximum, Is.EqualTo(expected.Background).Within(2e-12));
            Assert.That(actual.ResidualRms / maximum, Is.EqualTo(expected.ResidualRms).Within(2e-10));
        }
    }

}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AEther.Tests;

public class InputGainTests
{
    static AnalyzerOptions Options(double gainDb = 0) => new()
    {
        MinFrequency = 100, MaxFrequency = 1000, TimeResolution = 100,
        SIMDEnabled = false, InputGainDb = gainDb,
    };

    [TestCase(short.MinValue, -1.0)]
    [TestCase((short)0, 0.0)]
    [TestCase(short.MaxValue, 32767 / 32768.0)]
    public void SignedPcmUsesNormalizedFullScale(short sample, double expected)
        => Assert.That(SampleType.Int16.Instance.ReadFrom(BitConverter.GetBytes(sample)), Is.EqualTo(expected));

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    [TestCase(-60.001)]
    [TestCase(60.001)]
    public void RejectsInvalidGain(double gainDb)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new Analyzer(
            new SampleFormat(SampleType.Float32.Instance, 8000, 1), Options(gainDb)));

    [TestCase(-60)]
    [TestCase(0)]
    [TestCase(6.020599913279624)]
    [TestCase(60)]
    public async Task GainIsAppliedBeforeEveryAnalysisConsumerAndSnapshotAtConstruction(double gainDb)
    {
        var samples = Enumerable.Range(0, 1600).Select(i =>
            (float)(.25 * Math.Sin(2 * Math.PI * 200 * i / 8000) + .125 * Math.Sin(2 * Math.PI * 503 * i / 8000))).ToArray();
        var baseline = await Analyze(samples, 0);
        var actual = await Analyze(samples, gainDb, mutateOptions: true);
        var gain = Math.Pow(10, gainDb / 20);
        Assert.That(actual.Rows.Count, Is.EqualTo(baseline.Rows.Count));
        for (int row = 0; row < actual.Rows.Count; row++)
        {
            for (int i = 0; i < actual.Rows[row].Length; i++)
                Assert.That(actual.Rows[row][i], Is.EqualTo(baseline.Rows[row][i] * gain).Within(1e-9));
            Assert.That(actual.Features[row].Level, Is.EqualTo(baseline.Features[row].Level * gain).Within(1e-10));
            Assert.That(actual.Features[row].Bass, Is.EqualTo(baseline.Features[row].Bass * gain).Within(1e-10));
            Assert.That(actual.Features[row].Body, Is.EqualTo(baseline.Features[row].Body * gain).Within(1e-10));
            Assert.That(actual.Features[row].Air, Is.EqualTo(baseline.Features[row].Air * gain).Within(1e-10));
            Assert.That(actual.Features[row].MusicalTime,
                Is.EqualTo(baseline.Features[row].MusicalTime * gain * gain).Within(1e-8));
            for (int k = 0; k < actual.Evidence[row].Length; k++)
            {
                var a = actual.Evidence[row][k]; var b = baseline.Evidence[row][k];
                Assert.That(a.Sine, Is.EqualTo(b.Sine * gain).Within(1e-9));
                Assert.That(a.Transient, Is.EqualTo(b.Transient * gain).Within(1e-9));
                Assert.That(a.Background, Is.EqualTo(b.Background * gain).Within(1e-9));
                Assert.That(a.ResidualRms, Is.EqualTo(b.ResidualRms * gain).Within(1e-8));
                if (b.ResidualRms > 1e-6)
                {
                    Assert.That(a.NormalizedSine, Is.EqualTo(b.NormalizedSine).Within(1e-8 * Math.Max(1, Math.Abs(b.NormalizedSine))));
                    Assert.That(a.NormalizedTransient, Is.EqualTo(b.NormalizedTransient).Within(1e-8 * Math.Max(1, Math.Abs(b.NormalizedTransient))));
                }
            }
        }
    }

    [Test]
    public async Task NonfiniteSamplesBecomeSilenceAndFloatOverrangeIsPreserved()
    {
        var input = Enumerable.Repeat(2f, 80).ToArray();
        input[0] = float.NaN; input[1] = float.PositiveInfinity; input[2] = float.NegativeInfinity;
        var actual = await Analyze(input, 0);
        Assert.That(actual.Rows.Single().All(double.IsFinite), Is.True);
        Assert.That(actual.Features.Single().Level, Is.EqualTo(2 * Math.Sqrt(77 / 80.0)).Within(1e-12));
    }

    [Test]
    public async Task GainMatchesChangingTheSourceAmplitudeIncludingAttacks()
    {
        var input = Enumerable.Range(0, 800).Select(i => (float)(.125 * Math.Sin(i * .2))).ToArray();
        var gained = await Analyze(input, 20 * Math.Log10(2));
        var source = await Analyze(input.Select(x => 2 * x).ToArray(), 0);
        Assert.That(gained.Rows, Is.EqualTo(source.Rows));
        Assert.That(gained.Features, Is.EqualTo(source.Features));
        Assert.That(gained.Attacks, Is.EqualTo(source.Attacks));
    }

    sealed class Result
    {
        public readonly List<double[]> Rows = new();
        public readonly List<AudioFeatures> Features = new();
        public readonly List<JointEvidence[]> Evidence = new();
        public readonly List<double> Attacks = new();
    }

    static async Task<Result> Analyze(float[] input, double gainDb, bool mutateOptions = false)
    {
        var options = Options(gainDb);
        var analyzer = new Analyzer(new SampleFormat(SampleType.Float32.Instance, 8000, 1), options);
        if (mutateOptions) options.InputGainDb = gainDb == 0 ? 20 : 0;
        var result = new Result();
        analyzer.SamplesAnalyzed += (_, e) => result.Rows.Add(e.GetChannel(0).ToArray());
        analyzer.FeaturesAvailable += (_, e) => result.Features.Add(e);
        analyzer.JointEvidenceAvailable += (_, e) => result.Evidence.Add(e.GetChannel(0).ToArray());
        analyzer.TransientDetected += (_, e) => result.Attacks.Add(e);
        var bytes = new byte[input.Length * sizeof(float)];
        Buffer.BlockCopy(input, 0, bytes, 0, bytes.Length);
        analyzer.PostSamples(bytes);
        analyzer.Stop();
        await analyzer.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        return result;
    }
}

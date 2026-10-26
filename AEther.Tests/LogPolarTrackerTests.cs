using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace AEther.Tests;

public class LogPolarTrackerTests
{
    static double Wrap(double x) => Math.IEEERemainder(x, 2 * Math.PI);

    [TestCase(1000.0), TestCase(4000.0)]
    public void SmallTrackerSettingsDoNotAddASubstantialDelayToHighFrequencyAmplitudeSteps(double frequency)
    {
        const int fs = 48000, block = 48;
        var backend = new SwiftProcessor(new Domain(frequency, 12, 1), fs, false);
        var tracker = new LogPolarTracker(backend.Frequencies, .001,
            new() { AmplitudeTrackingSeconds = .001, TrackerNoiseFloor = 1e-6 });
        var input = new double[block]; var z = new Complex[1]; var output = new double[4];
        double raw90 = -1, inferred90 = -1;
        for (int n = 0; n < fs + fs / 10; n += block)
        {
            for (int i = 0; i < block; i++)
                input[i] = (n < fs ? .02 : .2) * Math.Sin(2 * Math.PI * frequency * (n + i) / fs);
            backend.Process(input); backend.OutputComplex(z); tracker.Process(z, output);
            if (n < fs) continue;
            double time = (n + block - fs) * 1000.0 / fs;
            if (raw90 < 0 && z[0].Magnitude >= .182) raw90 = time;
            if (inferred90 < 0 && Math.Exp(tracker.States.Span[0].LogAmplitude) >= .182) inferred90 = time;
        }
        Assert.That(raw90, Is.InRange(0, 20));
        Assert.That(inferred90, Is.InRange(0, raw90 + 3), "Tracker should closely follow the observed amplitude with these settings.");
    }

    [Test]
    public void PhaseJumpDoesNotPermanentlyCollapseConstantAmplitudeOrItsDerivative()
    {
        const double dt = .001;
        var tracker = new LogPolarTracker(new[] { 100.0 }, dt, new());
        var input = new Complex[1]; var output = new double[4];
        for (int n = 1; n <= 6000; n++)
        {
            input[0] = Complex.FromPolarCoordinates(.1, 2 * Math.PI * 100 * n * dt + (n > 1500 ? Math.PI : 0));
            tracker.Process(input, output);
        }
        var s = tracker.States.Span[0];
        TestContext.WriteLine($"Phase jump: a={s.LogAmplitude}, v={s.AmplitudeSlope}, w={s.FrequencyOffset}, green={output[1]}");
        Assert.That(s.LogAmplitude, Is.EqualTo(Math.Log(.1)).Within(.02));
        Assert.That(s.AmplitudeSlope, Is.EqualTo(0).Within(.02));
        Assert.That(output[1], Is.EqualTo(.5).Within(.002));
        Assert.That(Wrap(s.Phase - Math.PI), Is.EqualTo(0).Within(.02));
        Assert.That(s.FrequencyOffset, Is.EqualTo(0).Within(.02));
    }

    [Test]
    public void LongNonzeroDecayAndReattackRecoverWithoutResetOrAmplitudeGate()
    {
        const double dt = .001;
        var tracker = new LogPolarTracker(new[] { 100.0 }, dt, new());
        var input = new Complex[1]; var output = new double[4];
        for (int n = 1; n <= 9000; n++)
        {
            double t = n * dt;
            double amplitude = t < 1 || t >= 6 ? .1 : .1 * Math.Exp(-10 * (t - 1));
            double phase = t < 6 ? t : 6 - 2 * (t - 6);
            input[0] = Complex.FromPolarCoordinates(amplitude, 2 * Math.PI * 100 * t + phase);
            tracker.Process(input, output);
        }
        var s = tracker.States.Span[0];
        TestContext.WriteLine($"Reattack: a={s.LogAmplitude}, v={s.AmplitudeSlope}, w={s.FrequencyOffset}, green={output[1]}");
        Assert.That(s.LogAmplitude, Is.EqualTo(Math.Log(.1)).Within(.02));
        Assert.That(s.AmplitudeSlope, Is.EqualTo(0).Within(.02));
        Assert.That(output[1], Is.EqualTo(.5).Within(.002));
        Assert.That(s.FrequencyOffset, Is.EqualTo(-2).Within(.02), "Frequency derivative must recover too.");
    }

    [TestCase(1e-6), TestCase(.001), TestCase(.1), TestCase(1.0)]
    public void RawDiagnosticStateIsVisibleWithoutAmplitudeWeighting(double amplitude)
    {
        var tracker = new LogPolarTracker(new[] { 100.0 }, .01, new());
        var output = new double[4];
        tracker.Process(new[] { new Complex(amplitude, 0) }, output);
        // Squashing a/4 is sqrt(amplitude)/(1+sqrt(amplitude)). No second envelope.
        var root = Math.Sqrt(amplitude);
        Assert.That(output[0], Is.EqualTo(root / (1 + root)).Within(1e-12));
        Assert.That(output[1], Is.EqualTo(.5).Within(1e-12));
        Assert.That(output[2], Is.EqualTo(.5).Within(1e-12));
        Assert.That(output[3], Is.EqualTo(.5).Within(1e-12));
    }

    [Test]
    public void ResponseSettingInSecondsControlsAdaptationSpeed()
    {
        const double dt = .001;
        var fast = new LogPolarTracker(new[] { 100.0 }, dt, new() { AmplitudeTrackingSeconds = .05 });
        var slow = new LogPolarTracker(new[] { 100.0 }, dt, new() { AmplitudeTrackingSeconds = .2 });
        var input = new Complex[1]; var output = new double[4];
        for (int n = 1; n <= 1100; n++)
        {
            input[0] = Complex.FromPolarCoordinates(n <= 1000 ? .1 : .12, 2 * Math.PI * 100 * dt * n);
            fast.Process(input, output); slow.Process(input, output);
        }
        double target = Math.Log(.12);
        Assert.That(Math.Abs(fast.States.Span[0].LogAmplitude - target),
            Is.LessThan(Math.Abs(slow.States.Span[0].LogAmplitude - target)));
    }

    [TestCase(double.NaN), TestCase(double.PositiveInfinity), TestCase(0.0), TestCase(-1.0)]
    public void InvalidTrackerSettingsFailBeforeAnalysisStarts(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogPolarTracker(new[] { 100.0 }, .001,
            new() { AmplitudeTrackingSeconds = value }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogPolarTracker(new[] { 100.0 }, .001,
            new() { PhaseTrackingSeconds = value }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogPolarTracker(new[] { 100.0 }, .001,
            new() { TrackerNoiseFloor = value }));
    }

    [TestCase(.001), TestCase(.01), TestCase(1.0 / 60)]
    public void TracksExponentialAmplitudeAndCarrierRelativeFrequencyAcrossPhaseWraps(double dt)
    {
        const double carrier = 1400, slope = -.15, offset = 3;
        var tracker = new LogPolarTracker(new[] { carrier }, dt, new());
        var input = new Complex[1]; var output = new double[4];
        for (int n = 1; n <= (int)(4 / dt); n++)
        {
            double t = n * dt;
            input[0] = Complex.FromPolarCoordinates(Math.Exp(-.4 + slope * t),
                .7 + (2 * Math.PI * carrier + offset) * t);
            tracker.Process(input, output);
        }
        var s = tracker.States.Span[0];
        var end = (int)(4 / dt) * dt;
        Assert.That(s.LogAmplitude, Is.EqualTo(-.4 + slope * end).Within(.002));
        Assert.That(s.AmplitudeSlope, Is.EqualTo(slope).Within(.005));
        Assert.That(s.FrequencyOffset, Is.EqualTo(offset).Within(.005));
        Assert.That(Wrap(s.Phase - (.7 + offset * end)), Is.EqualTo(0).Within(.002));
        Assert.That(output, Is.All.InRange(0, 1));
    }

    [TestCase(true, true, -3.0), TestCase(true, false, 3.0)]
    [TestCase(false, true, -3.0), TestCase(false, false, 3.0)]
    public void ActualBackendPhaseRotationHasCorrectSignAndCarrier(bool swift, bool simd, double offset)
    {
        const int fs = 12000, block = 60;
        var options = new AnalyzerOptions { UseSwift = swift, SIMDEnabled = simd };
        var backend = SpectrumProcessor.Create(new Domain(400, 12, 1), fs, options);
        var tracker = new LogPolarTracker(backend.Frequencies, block / (double)fs, options);
        var input = new double[block]; var z = new Complex[1]; var output = new double[4];
        var center = backend.Frequencies.Span[0];
        for (int n = 0; n < 4 * fs; n += block)
        {
            for (int i = 0; i < block; i++) input[i] = .5 * Math.Sin(2 * Math.PI * (center + offset) * (n + i) / fs);
            backend.Process(input); backend.OutputComplex(z);
            tracker.Process(z, output);
        }
        Assert.That(tracker.States.Span[0].FrequencyOffset / (2 * Math.PI), Is.EqualTo(offset).Within(.03));
    }

    [TestCase(true), TestCase(false)]
    public void SteadyRealToneCentersSlopesAcrossObservableFilterbankBins(bool swift)
    {
        const int fs = 24000, block = 24;
        // Resolve the tiny off-band leakage too. Below a chosen measurement floor,
        // the requested constant-velocity model is intentionally allowed to coast.
        var options = new AnalyzerOptions { UseSwift = swift, TrackerNoiseFloor = 1e-8 };
        var backend = SpectrumProcessor.Create(options.Domain, fs, options);
        var tracker = new LogPolarTracker(backend.Frequencies, block / (double)fs, options);
        var input = new double[block]; var z = new Complex[options.Domain.Length];
        var output = new double[4 * z.Length]; var greenSum = new double[z.Length];
        int count = 0;
        for (int n = 0; n < 5 * fs; n += block)
        {
            for (int i = 0; i < block; i++) input[i] = .1 * Math.Sin(2 * Math.PI * 220 * (n + i) / fs);
            backend.Process(input); backend.OutputComplex(z); tracker.Process(z, output);
            if (n < 4 * fs) continue;
            count++;
            for (int k = 0; k < z.Length; k++) greenSum[k] += output[4 * k + 1];
        }
        int observable = 0;
        for (int k = 0; k < z.Length; k++)
        {
            Assert.That(tracker.States.Span[k].LogAmplitude, Is.InRange(-79, 39), "No amplitude-clamp lockup.");
            // The polar-noise approximation is local/high-SNR. Do not demand a
            // fast settled slope for bins whose leakage approaches the noise floor.
            if (tracker.States.Span[k].ObservedAmplitude < 100 * options.TrackerNoiseFloor) continue;
            observable++;
            Assert.That(greenSum[k] / count, Is.EqualTo(.5).Within(.025),
                $"Swift={swift}, bin={k}, f={backend.Frequencies.Span[k]}, state={tracker.States.Span[k]}");
        }
        Assert.That(observable, Is.GreaterThan(1));
    }

    [Test]
    public void SilenceAndInvalidInputClearAllChannelsAndRawStateThenReinitialize()
    {
        var tracker = new LogPolarTracker(new[] { 100.0 }, .01, new());
        var output = new double[4];
        foreach (var invalid in new[] { Complex.Zero, new Complex(double.NaN, 0), new Complex(0, double.PositiveInfinity) })
        {
            tracker.Process(new[] { new Complex(.2, .3) }, output);
            Assert.That(output.Any(x => x > 0), Is.True);
            tracker.Process(new[] { invalid }, output);
            Assert.That(output, Is.All.Zero);
            Assert.That(tracker.States.Span[0].Active, Is.False);
            tracker.Process(new[] { new Complex(.1, .2) }, output);
            Assert.That(tracker.States.Span[0].Observations, Is.EqualTo(1));
        }
        tracker.Reset();
        Assert.That(tracker.States.Span[0].Active, Is.False);
    }

    [Test]
    public void QuietAndExtremeFiniteObservationsDoNotGenerateNonfiniteStates()
    {
        var tracker = new LogPolarTracker(new[] { 100.0 }, .001, new());
        var output = new double[4];
        var random = new Random(731);
        foreach (double amplitude in new[] { 1e-300, 1e-10, 1.0, 1e200, 1e-200, .3 })
        {
            for (int i = 0; i < 2500; i++)
            {
                tracker.Process(new[] { Complex.FromPolarCoordinates(amplitude, random.NextDouble() * 2 * Math.PI) }, output);
                var s = tracker.States.Span[0];
                Assert.That(new[] { s.LogAmplitude, s.AmplitudeSlope, s.Phase, s.FrequencyOffset }.All(double.IsFinite), Is.True);
                Assert.That(output, Is.All.InRange(0, 1));
            }
        }
    }

    [Test]
    public void LargerMeasurementFloorReducesResponseToPhaseNoise()
    {
        var low = new LogPolarTracker(new[] { 100.0 }, .001, new() { TrackerNoiseFloor = .0001 });
        var high = new LogPolarTracker(new[] { 100.0 }, .001, new() { TrackerNoiseFloor = .1 });
        var output = new double[4]; var z = new Complex[1];
        double lowPower = 0, highPower = 0;
        var random = new Random(12);
        for (int i = 1; i <= 8000; i++)
        {
            z[0] = Complex.FromPolarCoordinates(.1, i * .2 * Math.PI + .2 * (random.NextDouble() - .5));
            low.Process(z, output); high.Process(z, output);
            if (i < 4000) continue;
            lowPower += Math.Pow(low.States.Span[0].FrequencyOffset, 2);
            highPower += Math.Pow(high.States.Span[0].FrequencyOffset, 2);
        }
        Assert.That(highPower, Is.LessThan(lowPower / 10));
    }

    [Test]
    public void BlockObserverAgreesWithIndependentDenseLogPolarKalmanFilter()
    {
        const double dt = .01, carrier = 100, noise = .001;
        var tracker = new LogPolarTracker(new[] { carrier }, dt, new());
        double[] x = [-.4, 0, .2, 0];
        double[,] p = { { 1, 0, 0, 0 }, { 0, 100, 0, 0 }, { 0, 0, Math.PI * Math.PI / 3, 0 }, { 0, 0, 0, 100 } };
        double[,] f = { { 1, dt, 0, 0 }, { 0, 1, 0, 0 }, { 0, 0, 1, dt }, { 0, 0, 0, 1 } };
        var output = new double[4]; var observation = new Complex[1];
        for (int n = 1; n <= 250; n++)
        {
            double t = (n - 1) * dt;
            var z = Complex.FromPolarCoordinates(Math.Exp(-.4 + .08 * t), .2 + .3 * t + .001 * Math.Sin(t * 7));
            observation[0] = z * Complex.FromPolarCoordinates(1, 2 * Math.PI * carrier * n * dt);
            tracker.Process(observation, output);
            if (n > 1)
            {
                x[0] += dt * x[1]; x[2] += dt * x[3];
                p = Multiply(Multiply(f, p), Transpose(f));
                // q = dt * (.001/.1)^2 / .1^4 = dt for these defaults.
                for (int j = 0; j < 4; j += 2)
                {
                    p[j, j] += dt * Math.Pow(dt, 3) / 3;
                    p[j, j + 1] += dt * dt * dt / 2;
                    p[j + 1, j] += dt * dt * dt / 2;
                    p[j + 1, j + 1] += dt * dt;
                }
                double[,] h = { { 1, 0, 0, 0 }, { 0, 0, 1, 0 } };
                var ph = Multiply(p, Transpose(h)); var s = Multiply(h, ph);
                var observationVariance = noise * noise / (z.Magnitude * z.Magnitude);
                s[0, 0] += observationVariance; s[1, 1] += observationVariance;
                var det = s[0, 0] * s[1, 1] - s[0, 1] * s[1, 0];
                double[,] inverse = { { s[1, 1] / det, -s[0, 1] / det }, { -s[1, 0] / det, s[0, 0] / det } };
                var k = Multiply(ph, inverse);
                double ea = Math.Log(z.Magnitude) - x[0], ep = Wrap(z.Phase - x[2]);
                for (int j = 0; j < 4; j++) x[j] += k[j, 0] * ea + k[j, 1] * ep;
                var kh = Multiply(k, h); var m = new double[4, 4];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) m[i, j] = (i == j ? 1 : 0) - kh[i, j];
                p = Multiply(Multiply(m, p), Transpose(m));
                var kk = Multiply(k, Transpose(k));
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) p[i, j] += observationVariance * kk[i, j];
            }
            var actual = tracker.States.Span[0];
            Assert.That(actual.LogAmplitude, Is.EqualTo(x[0]).Within(1e-8));
            Assert.That(actual.AmplitudeSlope, Is.EqualTo(x[1]).Within(1e-8));
            Assert.That(Wrap(actual.Phase - x[2]), Is.EqualTo(0).Within(1e-8));
            Assert.That(actual.FrequencyOffset, Is.EqualTo(x[3]).Within(1e-8));
        }
    }

    static double[,] Transpose(double[,] a)
    {
        var result = new double[a.GetLength(1), a.GetLength(0)];
        for (int i = 0; i < a.GetLength(0); i++) for (int j = 0; j < a.GetLength(1); j++) result[j, i] = a[i, j];
        return result;
    }

    static double[,] Multiply(double[,] a, double[,] b)
    {
        var result = new double[a.GetLength(0), b.GetLength(1)];
        for (int i = 0; i < result.GetLength(0); i++) for (int j = 0; j < result.GetLength(1); j++)
            for (int k = 0; k < a.GetLength(1); k++) result[i, j] += a[i, k] * b[k, j];
        return result;
    }
}

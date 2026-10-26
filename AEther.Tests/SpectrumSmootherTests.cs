using System;
using System.Linq;
using System.Numerics;
using NUnit.Framework;

namespace AEther.Tests;

public class SpectrumSmootherTests
{
    [TestCase(.000001), TestCase(.1), TestCase(2.0)]
    public void BothModesStartAtTheObservationInLinearAmplitudeUnits(double magnitude)
    {
        var smoother = new SpectrumSmoother(new[] { 100.0 }, .01, new());
        var a = new double[1]; var b = new double[1];
        smoother.Process(new[] { Complex.FromPolarCoordinates(magnitude, .7) }, a, b);
        Assert.That(a[0], Is.EqualTo(magnitude).Within(magnitude * 1e-14));
        Assert.That(b[0], Is.EqualTo(magnitude).Within(magnitude * 1e-14));
    }

    [Test]
    public void ZeroProcessNoiseMatchesTheTwoModelsAnalytically()
    {
        var smoother = new SpectrumSmoother(new[] { 100.0 }, .01, new()
            { LogAccelerationIntensity = 0, ComplexAccelerationIntensity = 0, SmoothingNoiseFloor = .001 });
        var a = new double[1]; var b = new double[1];
        smoother.Process(new[] { new Complex(.1, 0) }, a, b);
        smoother.Process(new[] { new Complex(.2, 0) }, a, b);
        // A: R1=.0001, R2=.000025, K=.8; B: equal R, K=.5.
        Assert.That(a[0], Is.EqualTo(Math.Exp(Math.Log(.1) + .8 * Math.Log(2))).Within(1e-14));
        Assert.That(b[0], Is.EqualTo(.15).Within(1e-14));
    }

    [Test]
    public void PhaseReversalOnlyChangesTheComplexModeAmplitude()
    {
        var smoother = new SpectrumSmoother(new[] { 100.0 }, .01, new()
            { LogAccelerationIntensity = 0, ComplexAccelerationIntensity = 0 });
        var a = new double[1]; var b = new double[1];
        smoother.Process(new[] { new Complex(.1, 0) }, a, b);
        smoother.Process(new[] { new Complex(-.1, 0) }, a, b);
        Assert.That(a[0], Is.EqualTo(.1).Within(1e-14));
        Assert.That(b[0], Is.EqualTo(0).Within(1e-14));
    }

    [TestCase(.001), TestCase(.01), TestCase(1.0 / 60)]
    public void KnownCarrierRotationPreservesAnOnCenterOscillation(double h)
    {
        const double frequency = 1234.5;
        var smoother = new SpectrumSmoother(new[] { frequency }, h, new());
        var input = new Complex[1]; var a = new double[1]; var b = new double[1];
        for (int n = 1; n <= 1000; n++)
        {
            input[0] = Complex.FromPolarCoordinates(.2, 2 * Math.PI * frequency * n * h + .8);
            smoother.Process(input, a, b);
            Assert.That(a[0], Is.EqualTo(.2).Within(1e-10));
            Assert.That(b[0], Is.EqualTo(.2).Within(1e-10));
        }
    }

    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true)]
    public void ComplexSmootherPreservesAnActualBackendOnCenterTone(bool swift, bool simd)
    {
        const int fs = 12000, block = 120;
        var options = new AnalyzerOptions { UseSwift = swift, SIMDEnabled = simd };
        var backend = SpectrumProcessor.Create(new Domain(400, 12, 1), fs, options);
        var smoother = new SpectrumSmoother(backend.Frequencies, block / (double)fs, options);
        double frequency = backend.Frequencies.Span[0];
        var samples = new double[block]; var input = new Complex[1]; var a = new double[1]; var b = new double[1];
        for (int n = 0; n < 5 * fs; n += block)
        {
            for (int i = 0; i < block; i++) samples[i] = .2 * Math.Sin(2 * Math.PI * frequency * (n + i) / fs);
            backend.Process(samples); backend.OutputComplex(input); smoother.Process(input, a, b);
        }
        Assert.That(a[0], Is.EqualTo(input[0].Magnitude).Within(.0001));
        Assert.That(b[0], Is.EqualTo(input[0].Magnitude).Within(.0001));
        Assert.That(b[0], Is.EqualTo(.2).Within(.0002));
    }

    [Test]
    public void LargerAccelerationIntensitiesTrackAnAmplitudeStepFaster()
    {
        var slow = new SpectrumSmoother(new[] { 100.0 }, .001, new());
        var fast = new SpectrumSmoother(new[] { 100.0 }, .001,
            new() { LogAccelerationIntensity = 1, ComplexAccelerationIntensity = .01 });
        var aSlow = new double[1]; var bSlow = new double[1]; var aFast = new double[1]; var bFast = new double[1];
        var input = new Complex[1];
        for (int n = 1; n <= 1020; n++)
        {
            input[0] = Complex.FromPolarCoordinates(n <= 1000 ? .1 : .2, 2 * Math.PI * 100 * n * .001);
            slow.Process(input, aSlow, bSlow); fast.Process(input, aFast, bFast);
        }
        Assert.That(Math.Abs(aFast[0] - .2), Is.LessThan(Math.Abs(aSlow[0] - .2)));
        Assert.That(Math.Abs(bFast[0] - .2), Is.LessThan(Math.Abs(bSlow[0] - .2)));
    }

    [Test]
    public void ComplexModeAgreesWithAnIndependentDenseFourStateKalmanFilter()
    {
        const double h = .01, frequency = 123.4, q = .00001, r = .000001;
        var smoother = new SpectrumSmoother(new[] { frequency }, h, new());
        var a = new double[1]; var b = new double[1]; var input = new Complex[1];
        double angle = 2 * Math.PI * Math.IEEERemainder(frequency * h, 1);
        double c = Math.Cos(angle), s = Math.Sin(angle);
        double[,] f = { { c, -s, h*c, -h*s }, { s, c, h*s, h*c }, { 0, 0, c, -s }, { 0, 0, s, c } };
        double[,] observation = { { 1, 0, 0, 0 }, { 0, 1, 0, 0 } };
        double[,] p = { { r, 0, 0, 0 }, { 0, r, 0, 0 }, { 0, 0, 0, 0 }, { 0, 0, 0, 0 } };
        double[] x = new double[4];
        for (int n = 0; n < 300; n++)
        {
            var z = Complex.FromPolarCoordinates(.2 + .05 * Math.Sin(n * .1), angle * n + .02 * n + (n > 150 ? 2 : 0));
            input[0] = z; smoother.Process(input, a, b);
            if (n == 0) { x[0] = z.Real; x[1] = z.Imaginary; }
            else
            {
                x = Enumerable.Range(0, 4).Select(i => Enumerable.Range(0, 4).Sum(j => f[i, j] * x[j])).ToArray();
                p = Multiply(Multiply(f, p), Transpose(f));
                for (int i = 0; i < 2; i++)
                {
                    p[i, i] += q * h*h*h / 3;
                    p[i, i+2] += q * h*h / 2; p[i+2, i] += q * h*h / 2;
                    p[i+2, i+2] += q * h;
                }
                var ph = Multiply(p, Transpose(observation));
                var innovation = Multiply(observation, ph);
                innovation[0, 0] += r; innovation[1, 1] += r;
                double det = innovation[0, 0] * innovation[1, 1] - innovation[0, 1] * innovation[1, 0];
                double[,] inverse = { { innovation[1, 1] / det, -innovation[0, 1] / det }, { -innovation[1, 0] / det, innovation[0, 0] / det } };
                var gain = Multiply(ph, inverse);
                double er = z.Real - x[0], ei = z.Imaginary - x[1];
                for (int i = 0; i < 4; i++) x[i] += gain[i, 0] * er + gain[i, 1] * ei;
                var kh = Multiply(gain, observation);
                var m = new double[4, 4];
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) m[i, j] = (i == j ? 1 : 0) - kh[i, j];
                p = Multiply(Multiply(m, p), Transpose(m));
                var kk = Multiply(gain, Transpose(gain));
                for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) p[i, j] += r * kk[i, j];
            }
            Assert.That(b[0], Is.EqualTo(Math.Sqrt(x[0]*x[0] + x[1]*x[1])).Within(1e-11));
        }
    }

    [Test]
    public void ConstantLogAmplitudeVelocityIsTrackedWithoutAnExtraDampingTerm()
    {
        var smoother = new SpectrumSmoother(new[] { 100.0 }, .001, new());
        var a = new double[1]; var b = new double[1]; var input = new Complex[1];
        for (int n = 1; n <= 5000; n++)
        {
            input[0] = new Complex(.1 * Math.Exp(.2 * n * .001), 0);
            smoother.Process(input, a, b);
        }
        Assert.That(a[0], Is.EqualTo(.1 * Math.Exp(1)).Within(1e-5));
    }

    [Test]
    public void ComplexSmoothingReducesCartesianObservationNoise()
    {
        var smoother = new SpectrumSmoother(new[] { 100.0 }, .01, new() { SmoothingNoiseFloor = .01 });
        var a = new double[1]; var b = new double[1]; var input = new Complex[1];
        var random = new Random(734); double rawError = 0, filteredError = 0;
        for (int n = 0; n < 3000; n++)
        {
            input[0] = new Complex(.2 + .02 * (random.NextDouble() - .5), .02 * (random.NextDouble() - .5));
            smoother.Process(input, a, b);
            if (n < 1000) continue;
            rawError += Math.Pow(input[0].Magnitude - .2, 2);
            filteredError += Math.Pow(b[0] - .2, 2);
        }
        Assert.That(filteredError, Is.LessThan(rawError * .2));
    }

    [Test]
    public void SilenceIsARealComplexMeasurementAndResetClearsBothModes()
    {
        var smoother = new SpectrumSmoother(new[] { 100.0 }, .01, new());
        var a = new double[1]; var b = new double[1];
        smoother.Process(new[] { new Complex(.2, 0) }, a, b);
        var silence = new Complex[1];
        smoother.Process(silence, a, b);
        Assert.That(a[0], Is.Zero);
        Assert.That(b[0], Is.InRange(.01, .2), "Complex state decays through its observation model, without a silence gate.");
        for (int n = 0; n < 3000; n++) smoother.Process(silence, a, b);
        Assert.That(b[0], Is.LessThan(1e-12));
        smoother.Reset(); smoother.Process(silence, a, b);
        Assert.That(a[0], Is.Zero); Assert.That(b[0], Is.Zero);
    }

    [Test]
    public void InvalidAndExtremeObservationsStayNumericallySafe()
    {
        var smoother = new SpectrumSmoother(new[] { 100.0 }, .001, new());
        var a = new double[1]; var b = new double[1];
        foreach (double amplitude in new[] { 1e-300, 1e200, 1e-200, .1 })
        {
            for (int n = 0; n < 20; n++)
            {
                smoother.Process(new[] { new Complex(amplitude, 0) }, a, b);
                Assert.That(double.IsFinite(a[0]) && a[0] >= 0, Is.True);
                Assert.That(double.IsFinite(b[0]) && b[0] >= 0, Is.True);
            }
        }
        smoother.Process(new[] { new Complex(double.NaN, 0) }, a, b);
        Assert.That(a[0], Is.Zero); Assert.That(b[0], Is.Zero);
        smoother.Process(new[] { new Complex(.1, 0) }, a, b);
        Assert.That(a[0], Is.EqualTo(.1).Within(1e-14)); Assert.That(b[0], Is.EqualTo(.1));
    }

    [TestCase(double.NaN), TestCase(double.PositiveInfinity), TestCase(-1.0)]
    public void InvalidModelParametersAreRejected(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumSmoother(new[] { 100.0 }, .01, new() { LogAccelerationIntensity = value }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumSmoother(new[] { 100.0 }, .01, new() { ComplexAccelerationIntensity = value }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpectrumSmoother(new[] { 100.0 }, .01, new() { SmoothingNoiseFloor = value }));
    }

    static double[,] Multiply(double[,] a, double[,] b)
    {
        var c = new double[a.GetLength(0), b.GetLength(1)];
        for (int i = 0; i < c.GetLength(0); i++) for (int j = 0; j < c.GetLength(1); j++)
            for (int k = 0; k < a.GetLength(1); k++) c[i, j] += a[i, k] * b[k, j];
        return c;
    }
    static double[,] Transpose(double[,] a)
    {
        var b = new double[a.GetLength(1), a.GetLength(0)];
        for (int i = 0; i < a.GetLength(0); i++) for (int j = 0; j < a.GetLength(1); j++) b[j, i] = a[i, j];
        return b;
    }
}

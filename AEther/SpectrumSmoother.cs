using System;
using System.ComponentModel;
using System.Numerics;

namespace AEther;

public enum SmoothingMode
{
    [Description("A: log amplitude")]
    LogAmplitude,
    [Description("B: complex resonator")]
    Complex,
}

/// <summary>Independent CWNA Kalman filters. Both outputs are linear magnitudes;
/// velocity is internal. Complex dynamics include the known carrier rotation.</summary>
public sealed class SpectrumSmoother
{
    readonly double h, logQ, complexQ, noise;
    readonly Complex[] rotation;
    readonly LogBin[] logBins;
    readonly ComplexBin[] complexBins;

    public SpectrumSmoother(ReadOnlyMemory<double> frequencies, double observationSeconds, AnalyzerOptions options)
    {
        options.ValidateSmoother();
        if (frequencies.IsEmpty) throw new ArgumentException("At least one carrier is required.", nameof(frequencies));
        if (!double.IsFinite(observationSeconds) || observationSeconds <= 0 || observationSeconds > 1)
            throw new ArgumentOutOfRangeException(nameof(observationSeconds));
        h = observationSeconds;
        logQ = options.LogAccelerationIntensity; complexQ = options.ComplexAccelerationIntensity;
        noise = options.SmoothingNoiseFloor;
        rotation = new Complex[frequencies.Length];
        logBins = new LogBin[rotation.Length]; complexBins = new ComplexBin[rotation.Length];
        for (int k = 0; k < rotation.Length; k++)
        {
            double f = frequencies.Span[k];
            if (!double.IsFinite(f) || f <= 0) throw new ArgumentOutOfRangeException(nameof(frequencies));
            rotation[k] = Complex.FromPolarCoordinates(1, 2 * Math.PI * Math.IEEERemainder(f * h, 1));
        }
    }

    public void Reset() { Array.Clear(logBins); Array.Clear(complexBins); }

    public void Process(ReadOnlyMemory<Complex> input, Memory<double> logAmplitude, Memory<double> complexAmplitude)
    {
        if (input.Length != rotation.Length || logAmplitude.Length != rotation.Length || complexAmplitude.Length != rotation.Length)
            throw new ArgumentException("Spectrum smoother dimensions do not match.");
        for (int k = 0; k < rotation.Length; k++)
        {
            var z = input.Span[k];
            double magnitude = z.Magnitude;
            if (!Finite(z) || !double.IsFinite(magnitude))
            {
                logBins[k] = default; complexBins[k] = default;
                logAmplitude.Span[k] = complexAmplitude.Span[k] = 0;
                continue;
            }
            // Zero has no logarithm. Mode B can observe zero normally and decay
            // according to its model; mode A clears at exact backend silence.
            ref var a = ref logBins[k];
            if (magnitude == 0)
            {
                a = default; logAmplitude.Span[k] = 0;
            }
            else
            {
                double measured = Math.Log(magnitude);
                double r = Variance(noise / magnitude);
                if (!a.Ready) a = new() { Position = measured, Covariance = new(r), Ready = true };
                else
                {
                    a.Position += h * a.Velocity;
                    a.Covariance.Predict(h, logQ);
                    a.Covariance.Correct(r, out double k0, out double k1);
                    double error = measured - a.Position;
                    a.Position += k0 * error; a.Velocity += k1 * error;
                }
                double result = Math.Exp(a.Position);
                // Arithmetic-domain guard only: no amplitude gate, slope clamp,
                // phase rejection or damping. Reacquire if finite input overflowed.
                if (!double.IsFinite(result) || !double.IsFinite(a.Velocity))
                {
                    a = new() { Position = measured, Covariance = new(r), Ready = true };
                    result = magnitude;
                }
                logAmplitude.Span[k] = result;
            }

            ref var b = ref complexBins[k];
            double complexR = noise * noise;
            if (!b.Ready) b = new() { Position = z, Covariance = new(complexR), Ready = true };
            else
            {
                b.Position = rotation[k] * (b.Position + h * b.Velocity);
                b.Velocity *= rotation[k];
                b.Covariance.Predict(h, complexQ);
                b.Covariance.Correct(complexR, out double k0, out double k1);
                var error = z - b.Position;
                b.Position += k0 * error; b.Velocity += k1 * error;
            }
            double amplitude = b.Position.Magnitude;
            if (!Finite(b.Position) || !Finite(b.Velocity) || !double.IsFinite(amplitude))
            {
                b = new() { Position = z, Covariance = new(complexR), Ready = true };
                amplitude = magnitude;
            }
            complexAmplitude.Span[k] = amplitude;
        }
    }

    static bool Finite(Complex z) => double.IsFinite(z.Real) && double.IsFinite(z.Imaginary);
    // Protect only the representable covariance range, not an audible SNR range.
    static double Variance(double ratio) => Math.Pow(Math.Clamp(ratio, 1e-150, 1e150), 2);

    struct LogBin { public double Position, Velocity; public Covariance Covariance; public bool Ready; }
    struct ComplexBin { public Complex Position, Velocity; public Covariance Covariance; public bool Ready; }

    // For B, each entry multiplies a 2x2 identity over real/imaginary parts.
    // Isotropic noise and a shared rotation preserve this structure exactly.
    struct Covariance(double positionVariance)
    {
        double p00 = positionVariance, p01, p11;
        // Initial position is the first observation, variance R. Initial velocity
        // is exactly zero (start-at-rest prior); process noise then grows uncertainty.
        public void Predict(double h, double q)
        {
            p00 += 2 * h * p01 + h * h * p11 + q * h * h * h / 3;
            p01 += h * p11 + q * h * h / 2;
            p11 += q * h;
        }
        public void Correct(double r, out double k0, out double k1)
        {
            double s = p00 + r;
            k0 = p00 / s; k1 = p01 / s;
            double t = 1 - k0;
            double n00 = t * t * p00 + k0 * k0 * r;
            double n01 = t * (p01 - k1 * p00) + k0 * k1 * r;
            double n11 = p11 - 2 * k1 * p01 + k1 * k1 * p00 + k1 * k1 * r;
            p00 = Math.Max(0, n00); p11 = Math.Max(0, n11);
            double bound = Math.Sqrt(p00) * Math.Sqrt(p11);
            p01 = Math.Clamp(n01, -bound, bound);
        }
    }
}

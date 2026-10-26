using System;
using System.Numerics;

namespace AEther;

/// <summary>Signed experimental state, before display squashing. Phase is carrier-relative,
/// modulo a whole-turn gauge; FrequencyOffset is its derivative in rad/s.</summary>
public readonly record struct LogPolarState(double LogAmplitude, double AmplitudeSlope,
    double Phase, double FrequencyOffset, double ObservedAmplitude, int Observations)
{
    public bool Active => Observations > 0;
}

/// <summary>Log-polar state with continuous white-noise acceleration. Complex measurements
/// are transformed to log magnitude and phase; their local noise variance is sigma^2/|z|^2.
/// Two 2x2 Kalman blocks retain uncertainty without coupling amplitude to phase lock.</summary>
public sealed class LogPolarTracker
{
    public const int OutputStride = 4;
    const double Tau = 2 * Math.PI;
    // Numerical domain, deliberately far outside musical ranges. No phase-jump rejection.
    const double MinLog = -80, MaxLog = 40, MaxSlope = 1e6;
    readonly double h, noiseFloor, qa, qp, amplitudeSeconds, phaseSeconds;
    readonly double[] carrier, advance;
    readonly Bin[] bins;
    readonly LogPolarState[] states;
    public ReadOnlyMemory<LogPolarState> States => states;

    public LogPolarTracker(ReadOnlyMemory<double> frequencies, double observationSeconds, AnalyzerOptions options)
    {
        options.ValidateTracker();
        if (frequencies.IsEmpty) throw new ArgumentException("At least one carrier is required.", nameof(frequencies));
        if (!double.IsFinite(observationSeconds) || observationSeconds <= 0 || observationSeconds > 1)
            throw new ArgumentOutOfRangeException(nameof(observationSeconds));
        h = observationSeconds;
        amplitudeSeconds = options.AmplitudeTrackingSeconds;
        phaseSeconds = options.PhaseTrackingSeconds;
        noiseFloor = options.TrackerNoiseFloor;
        // Reference amplitude=.1, Cartesian noise RMS=.001. h accounts for the
        // observation density so response times remain approximately rate invariant.
        // Keep the reference fixed when the user changes the measurement floor.
        qa = h * 1e-4 / Math.Pow(amplitudeSeconds, 4);
        qp = h * 1e-4 / Math.Pow(phaseSeconds, 4);
        bins = new Bin[frequencies.Length]; states = new LogPolarState[bins.Length];
        carrier = new double[bins.Length]; advance = new double[bins.Length];
        for (int k = 0; k < bins.Length; k++)
        {
            var f = frequencies.Span[k];
            if (!double.IsFinite(f) || f <= 0) throw new ArgumentOutOfRangeException(nameof(frequencies));
            advance[k] = Tau * Math.IEEERemainder(f * h, 1);
        }
    }

    public void Reset()
    {
        Array.Clear(bins); Array.Clear(states); Array.Clear(carrier);
    }

    /// <summary>One completed audio tick. Complex input has coherent amplitude normalization.
    /// Outputs encode [a,a',phase,phase'] directly in [0,1], without amplitude weighting.
    /// No allocation, neighborhood fit, phase gating, or outlier rejection.</summary>
    public void Process(ReadOnlyMemory<Complex> input, Memory<double> output)
    {
        if (input.Length != bins.Length || output.Length < OutputStride * bins.Length)
            throw new ArgumentException("Tracker spectrum dimensions do not match.");
        for (int k = 0; k < bins.Length; k++)
        {
            carrier[k] = Wrap(carrier[k] + advance[k]);
            var z = input.Span[k];
            var dst = output.Span.Slice(OutputStride * k, OutputStride);
            // Exact backend silence has no log or phase. The backend owns natural
            // tail decay; clearing here does not impose an amplitude threshold.
            if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary) || z == Complex.Zero)
            {
                bins[k] = default; states[k] = default; dst.Clear(); continue;
            }
            var magnitude = z.Magnitude;
            if (!double.IsFinite(magnitude))
            {
                bins[k] = default; states[k] = default; dst.Clear(); continue;
            }
            var measuredA = Math.Clamp(Math.Log(magnitude), MinLog, MaxLog);
            var measuredP = Wrap(z.Phase - carrier[k]);
            // First-order propagation of isotropic Cartesian measurement noise.
            // Use OBSERVED magnitude: a stale tiny prediction must not make a
            // newly strong observation unobservable. Bounds only protect arithmetic.
            var relativeNoise = Math.Clamp(noiseFloor / magnitude, 1e-12, 1e12);
            var observationVariance = relativeNoise * relativeNoise;
            ref var s = ref bins[k];
            if (s.Count == 0)
            {
                s.A = measuredA;
                s.P = measuredP;
                var uncertainty = Math.Max(1, observationVariance);
                s.Amplitude = new Covariance(uncertainty, 0, 1 / (amplitudeSeconds * amplitudeSeconds));
                // A phase with no information has a circular, not an unbounded
                // linear prior. Tiny startup magnitudes must not invent enormous
                // velocity uncertainty from Cartesian noise divided by |z|^2.
                s.Phase = new Covariance(Math.PI * Math.PI / 3, 0, 1 / (phaseSeconds * phaseSeconds));
            }
            else
            {
                s.A = Math.Clamp(s.A + h * s.V, MinLog, MaxLog);
                s.P = Wrap(s.P + h * s.W);
                s.Amplitude.Predict(h, qa);
                s.Phase.Predict(h, qp);
                s.Amplitude.Correct(observationVariance, measuredA - s.A, out var da, out var dv);
                s.Phase.Correct(observationVariance, Wrap(measuredP - s.P), out var dp, out var dw);
                s.A = Math.Clamp(s.A + da, MinLog, MaxLog);
                s.V = Math.Clamp(s.V + dv, -MaxSlope, MaxSlope);
                s.P = Wrap(s.P + dp);
                s.W = Math.Clamp(s.W + dw, -MaxSlope, MaxSlope);
            }
            if (s.Count < int.MaxValue) s.Count++;
            states[k] = new(s.A, s.V, s.P, s.W, magnitude, s.Count);
            dst[0] = Squash(s.A / 4);
            dst[1] = Squash(s.V * amplitudeSeconds);
            dst[2] = .5 + s.P / Tau;
            dst[3] = Squash(s.W * phaseSeconds);
        }
    }

    static double Wrap(double angle) => Math.IEEERemainder(angle, Tau);
    static double Squash(double value) => .5 + .5 * Math.Tanh(value);

    struct Bin
    {
        public double A, V, P, W;
        public Covariance Amplitude, Phase;
        public int Count;
    }

    struct Covariance(double c00, double c01, double c11)
    {
        double c00 = c00, c01 = c01, c11 = c11;
        public void Predict(double dt, double q)
        {
            c00 += 2 * dt * c01 + dt * dt * c11 + q * dt * dt * dt / 3;
            c01 += dt * c11 + q * dt * dt / 2;
            c11 += q * dt;
        }

        public void Correct(double noise, double error, out double dx, out double dv)
        {
            double s = c00 + noise;
            double k0 = c00 / s, k1 = c01 / s;
            dx = k0 * error; dv = k1 * error;
            // Scalar Joseph update. Expanded from (I-KH)P(I-KH)' + K R K'.
            double t = 1 - k0, u = -k1;
            double p00 = t * t * c00 + k0 * k0 * noise;
            double p01 = t * (u * c00 + c01) + k0 * k1 * noise;
            double p11 = u * u * c00 + 2 * u * c01 + c11 + k1 * k1 * noise;
            c00 = Math.Max(0, p00); c11 = Math.Max(0, p11);
            // Roundoff must not turn a PSD block indefinite.
            double bound = Math.Sqrt(c00) * Math.Sqrt(c11);
            c01 = Math.Clamp(p01, -bound, bound);
        }
    }
}

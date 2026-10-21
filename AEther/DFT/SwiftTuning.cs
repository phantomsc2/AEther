using System;
using System.Numerics;

namespace AEther;

/// <summary>Resolved cascade time constants and exact-silence expiry for one frequency bin.</summary>
public readonly record struct SwiftBand(double Frequency, double SlowSeconds, double FastSeconds,
    long SilenceSamples);

/// <summary>Construction-time tuning and analytic complex-tone responses.</summary>
public static class SwiftTuning
{
    /// <summary>
    /// Match the full half-power bandwidth to widthScale times the geometric log-bin width.
    /// Imbalance d splits S into ts=S*(1+d)/2 and tf=S*(1-d)/2; S is retuned for every d.
    /// The response is symmetric in Hz, so the two asymmetric log-bin edges need not be half-power.
    /// </summary>
    public static SwiftBand FromBandwidth(double frequency, int resolution, double sampleRate,
        double widthScale = 1, double imbalance = 0)
    {
        ValidateFrequency(frequency, sampleRate);
        if (!double.IsFinite(imbalance) || imbalance < 0 || imbalance > 1)
            throw new ArgumentOutOfRangeException(nameof(imbalance));
        var bandwidth = Bandwidth(frequency, resolution, widthScale);
        if (!double.IsFinite(bandwidth) || bandwidth <= 0 || bandwidth >= sampleRate)
            throw new ArgumentOutOfRangeException(nameof(widthScale),
                "SWIFT bandwidth must be positive and smaller than the sample rate. Reduce width or increase bins per octave.");
        var sine = 4 * Math.Pow(Math.Sin(Math.PI * bandwidth / (2 * sampleRate)), 2);
        double EdgePower(double sum)
        {
            var (a, ca) = Decay(sum * ((1 + imbalance) / 2), sampleRate);
            var (b, cb) = Decay(sum * ((1 - imbalance) / 2), sampleRate);
            return ca * ca / (ca * ca + a * sine) * (cb * cb / (cb * cb + b * sine));
        }
        // Construction only: use the same exact-response solve for equal, unequal and single-pole stages.
        double lo = 0, hi = 8 / frequency;
        while (double.IsFinite(hi) && EdgePower(hi) > .5) hi *= 2;
        if (!double.IsFinite(hi)) throw new ArgumentOutOfRangeException(nameof(frequency));
        for (var i = 0; i < 64; ++i)
        {
            var mid = lo + (hi - lo) / 2;
            if (EdgePower(mid) > .5) lo = mid; else hi = mid;
        }
        var sum = lo + (hi - lo) / 2;
        return FromDecay(frequency, sampleRate, sum * ((1 + imbalance) / 2), sum * ((1 - imbalance) / 2));
    }

    internal static double Bandwidth(double frequency, int resolution, double widthScale)
    {
        if (resolution <= 0) throw new ArgumentOutOfRangeException(nameof(resolution));
        if (!double.IsFinite(widthScale) || widthScale <= 0) throw new ArgumentOutOfRangeException(nameof(widthScale));
        return widthScale * frequency * (2 * Math.Sinh(Math.Log(2) / (2.0 * resolution)));
    }

    public static SwiftBand FromCycles(double frequency, double sampleRate, double slowCycles = 5, double fastCycles = 3)
    {
        if (!double.IsFinite(slowCycles) || slowCycles <= 0 || !double.IsFinite(fastCycles)
            || fastCycles <= 0 || fastCycles > slowCycles) throw new ArgumentOutOfRangeException(nameof(slowCycles));
        ValidateFrequency(frequency, sampleRate);
        return FromDecay(frequency, sampleRate, slowCycles / frequency, fastCycles / frequency);
    }

    static SwiftBand FromDecay(double f, double fs, double slow, double fast) =>
        new(f, slow, fast, checked(2 + (long)Math.Ceiling(48 * fs * slow)));

    static void ValidateFrequency(double f, double fs)
    {
        if (!double.IsFinite(fs) || fs <= 0) throw new ArgumentOutOfRangeException(nameof(fs));
        if (!double.IsFinite(f) || f <= 0 || f > fs / 2) throw new ArgumentOutOfRangeException(nameof(f));
    }

    internal static (double Pole, double Gain) Decay(double seconds, double sampleRate)
    {
        if (seconds == 0) return (0, 1);
        var x = -1 / (sampleRate * seconds);
        // Avoid cancellation at long decay constants without relying on the
        // platform's ExpM1 implementation. The series error is below double precision.
        var gain = Math.Abs(x) < 1e-4
            ? -x * (1 + x * (.5 + x * (1.0 / 6 + x * (1.0 / 24 + x / 120))))
            : 1 - Math.Exp(x);
        return (Math.Exp(x), gain);
    }

    public static Complex Response(double offsetHz, double sampleRate, double slowSeconds, double fastSeconds)
    {
        var (a, ca) = Decay(slowSeconds, sampleRate);
        var (b, cb) = Decay(fastSeconds, sampleRate);
        var angle = -2 * Math.PI * offsetHz / sampleRate;
        var e = Complex.FromPolarCoordinates(1, angle);
        var delta = new Complex(2 * Math.Pow(Math.Sin(angle / 2), 2), -Math.Sin(angle));
        return e * (ca / (ca + a * delta)) * (cb / (cb + b * delta));
    }

    public static Complex HannResponse(double offsetHz, double sampleRate, int length) =>
        (Dirichlet(offsetHz / sampleRate, length)
         - .5 * Dirichlet(offsetHz / sampleRate - 1.0 / length, length)
         - .5 * Dirichlet(offsetHz / sampleRate + 1.0 / length, length)) / length;

    static Complex Dirichlet(double u, int n)
    {
        u -= Math.Round(u);
        if (Math.Abs(u) < 1e-15) return new Complex(n, 0);
        return Complex.FromPolarCoordinates(1, -Math.PI * (n - 1) * u)
            * (Math.Sin(Math.PI * n * u) / Math.Sin(Math.PI * u));
    }
}

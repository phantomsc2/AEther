using System;
using System.Linq;
using System.Numerics;

namespace AEther;

/// <summary>
/// Normalized alpha-SWIFT: two complex one-pole smoothers followed by one sample of delay.
/// SIMD lanes are frequency bins; no delayed sample or magnitude-history lookups are needed.
/// Exact zero input truncates the remaining tail after 48 slow time constants, never by amplitude.
/// </summary>
public sealed class SwiftProcessor : ISpectrumProcessor
{
    readonly SwiftBand[] bands;
    readonly double[] pr, pi, qr, qi, yr, yi, ar, ai, br, bi, ca, cb, rr, ri, expiry;
    readonly bool vectorized;
    readonly long maxExpiry;
    long silent;
    public ReadOnlyMemory<SwiftBand> Bands => bands;
    public bool IsVectorized => vectorized;

    public SwiftProcessor(Domain domain, double sampleRate, bool useSIMD,
        double widthScale = 1, double imbalance = 0)
        : this(domain.Select(f => SwiftTuning.FromBandwidth(f, domain.Resolution, sampleRate,
            widthScale, imbalance)).ToArray(), sampleRate, useSIMD) { }

    /// <summary>Explicit resolved settings, also used for fixed-cycle research comparisons.</summary>
    public SwiftProcessor(ReadOnlyMemory<SwiftBand> settings, double sampleRate, bool useSIMD)
    {
        if (!double.IsFinite(sampleRate) || sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (settings.IsEmpty) throw new ArgumentException("At least one bin is required.", nameof(settings));
        bands = settings.ToArray();
        vectorized = useSIMD && Vector.IsHardwareAccelerated;
        var size = (bands.Length + Vector<double>.Count - 1) / Vector<double>.Count * Vector<double>.Count;
        pr = new double[size]; pi = new double[size]; qr = new double[size]; qi = new double[size];
        yr = new double[size]; yi = new double[size];
        ar = new double[size]; ai = new double[size]; br = new double[size]; bi = new double[size];
        ca = new double[size]; cb = new double[size]; rr = new double[size]; ri = new double[size]; expiry = new double[size];
        Array.Fill(expiry, 1);
        for (var k = 0; k < bands.Length; ++k)
        {
            var band = bands[k];
            if (!double.IsFinite(band.Frequency) || band.Frequency <= 0 || band.Frequency > sampleRate / 2
                || !double.IsFinite(band.SlowSeconds) || band.SlowSeconds < 0
                || !double.IsFinite(band.FastSeconds) || band.FastSeconds < 0
                || band.FastSeconds > band.SlowSeconds
                || band.SilenceSamples != checked(2 + (long)Math.Ceiling(48 * sampleRate * band.SlowSeconds)))
                throw new ArgumentException("Invalid resolved SWIFT bin.", nameof(settings));
            var angle = 2 * Math.PI * band.Frequency / sampleRate;
            rr[k] = Math.Cos(angle); ri[k] = Math.Sin(angle);
            var (a, gainA) = SwiftTuning.Decay(band.SlowSeconds, sampleRate);
            var (b, gainB) = SwiftTuning.Decay(band.FastSeconds, sampleRate);
            ar[k] = a * rr[k]; ai[k] = a * ri[k]; br[k] = b * rr[k]; bi[k] = b * ri[k];
            ca[k] = gainA; cb[k] = gainB; expiry[k] = band.SilenceSamples;
            maxExpiry = Math.Max(maxExpiry, band.SilenceSamples);
        }
    }

    public void Reset()
    {
        Array.Clear(pr); Array.Clear(pi); Array.Clear(qr); Array.Clear(qi); Array.Clear(yr); Array.Clear(yi);
        silent = 0;
    }

    public void Process(ReadOnlyMemory<double> samples)
    {
        if (samples.IsEmpty) return;
        if (vectorized) ProcessVectors(samples.Span); else ProcessScalar(samples.Span);
    }

    void ProcessScalar(ReadOnlySpan<double> samples)
    {
        var nextSilent = silent;
        for (var k = 0; k < bands.Length; ++k)
        {
            var p = pr[k]; var ip = pi[k]; var q = qr[k]; var iq = qi[k];
            var y = yr[k]; var iy = yi[k];
            nextSilent = silent;
            foreach (var x in samples)
            {
                nextSilent = x == 0 ? Math.Min(nextSilent + 1, maxExpiry) : 0;
                y = q; iy = iq;
                var np = ar[k] * p - ai[k] * ip + ca[k] * x;
                ip = ar[k] * ip + ai[k] * p; p = np;
                var nq = br[k] * q - bi[k] * iq + cb[k] * p;
                iq = br[k] * iq + bi[k] * q + cb[k] * ip; q = nq;
                if (nextSilent >= expiry[k]) p = ip = q = iq = y = iy = 0;
            }
            pr[k] = p; pi[k] = ip; qr[k] = q; qi[k] = iq;
            yr[k] = rr[k] * y - ri[k] * iy; yi[k] = rr[k] * iy + ri[k] * y;
        }
        silent = nextSilent;
    }

    void ProcessVectors(ReadOnlySpan<double> samples)
    {
        var nextSilent = silent;
        for (var k = 0; k < pr.Length; k += Vector<double>.Count)
        {
            var p = new Vector<double>(pr, k); var ip = new Vector<double>(pi, k);
            var q = new Vector<double>(qr, k); var iq = new Vector<double>(qi, k);
            var y = new Vector<double>(yr, k); var iy = new Vector<double>(yi, k);
            var a = new Vector<double>(ar, k); var ia = new Vector<double>(ai, k);
            var b = new Vector<double>(br, k); var ib = new Vector<double>(bi, k);
            var ga = new Vector<double>(ca, k); var gb = new Vector<double>(cb, k);
            var r = new Vector<double>(rr, k); var ir = new Vector<double>(ri, k);
            var limit = new Vector<double>(expiry, k);
            nextSilent = silent;
            foreach (var sample in samples)
            {
                nextSilent = sample == 0 ? Math.Min(nextSilent + 1, maxExpiry) : 0;
                var x = new Vector<double>(sample);
                y = q; iy = iq;
                var np = a * p - ia * ip + ga * x;
                ip = a * ip + ia * p; p = np;
                var nq = b * q - ib * iq + gb * p;
                iq = b * iq + ib * q + gb * ip; q = nq;
                if (nextSilent > 0)
                {
                    var keep = Vector.LessThan(new Vector<double>(nextSilent), limit);
                    p = Vector.ConditionalSelect(keep, p, Vector<double>.Zero);
                    ip = Vector.ConditionalSelect(keep, ip, Vector<double>.Zero);
                    q = Vector.ConditionalSelect(keep, q, Vector<double>.Zero);
                    iq = Vector.ConditionalSelect(keep, iq, Vector<double>.Zero);
                    y = Vector.ConditionalSelect(keep, y, Vector<double>.Zero);
                    iy = Vector.ConditionalSelect(keep, iy, Vector<double>.Zero);
                }
            }
            p.CopyTo(pr, k); ip.CopyTo(pi, k); q.CopyTo(qr, k); iq.CopyTo(qi, k);
            (r * y - ir * iy).CopyTo(yr, k); (r * iy + ir * y).CopyTo(yi, k);
        }
        silent = nextSilent;
    }

    public Complex GetOutput(int bin) => (uint)bin < (uint)bands.Length
        ? new Complex(yr[bin], yi[bin]) : throw new ArgumentOutOfRangeException(nameof(bin));

    public void OutputLinear(Memory<double> destination)
    {
        if (destination.Length < bands.Length) throw new ArgumentException("Spectrum destination is too short.", nameof(destination));
        for (var k = 0; k < bands.Length; ++k)
            destination.Span[k] = SpectralMath.Sanitize(2 * GetOutput(k).Magnitude);
    }

    public void Output(Memory<double> destination)
    {
        OutputLinear(destination);
        for (var k = 0; k < bands.Length; ++k) destination.Span[k] = Math.Log10(destination.Span[k]);
    }
}

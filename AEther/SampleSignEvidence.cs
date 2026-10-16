using System;
using System.Numerics;

namespace AEther;

/// <summary>Per-sample stochastic median estimates in linear amplitude units.
/// T remains an instantaneous frequency median, without temporal smoothing.</summary>
internal sealed class SampleSignEvidence
{
    readonly int Bands, Radius, Groups;
    readonly double Step;
    readonly EvidenceOutput Presentation;
    readonly double[] Magnitude, Directional, Scratch;
    readonly bool SubtractBackground;
    readonly Vector<double>[] Time, Joint, InverseWidths, Masks;

    public SampleSignEvidence(Domain domain, AnalyzerOptions options, double sampleRate)
    {
        options.ValidateSplitter(domain.Resolution, sampleRate);
        Bands = domain.Length;
        var width = Math.Min(Math.Max(1, (int)Math.Min(65535,
            Math.Round(options.MedianWidth * domain.Resolution))) | 1, Bands);
        Radius = width / 2;
        Groups = (Bands + Vector<double>.Count - 1) / Vector<double>.Count;
        Magnitude = new double[Groups * Vector<double>.Count + 2 * Radius];
        Directional = new double[Magnitude.Length];
        SubtractBackground = options.SubtractNoiseBackground;
        Scratch = new double[Math.Min(Bands, 2 * Radius + 1)];
        Time = new Vector<double>[Groups]; Joint = new Vector<double>[Groups];
        InverseWidths = new Vector<double>[Groups];
        Masks = new Vector<double>[Groups * (2 * Radius + 1)];
        Step = options.SignSlewRate / sampleRate;
        Presentation = new EvidenceOutput(options);
        var lanes = new double[Vector<double>.Count];
        for (var g = 0; g < Groups; ++g)
        {
            for (var lane = 0; lane < lanes.Length; ++lane)
            {
                var k = g * lanes.Length + lane;
                lanes[lane] = k < Bands ? 1.0 / (Math.Min(Bands - 1, k + Radius)
                    - Math.Max(0, k - Radius) + 1) : 0;
            }
            InverseWidths[g] = new Vector<double>(lanes);
            for (var dx = -Radius; dx <= Radius; ++dx)
            {
                for (var lane = 0; lane < lanes.Length; ++lane)
                {
                    var k = g * lanes.Length + lane;
                    lanes[lane] = k < Bands && k + dx >= 0 && k + dx < Bands ? 1 : 0;
                }
                Masks[g * (2 * Radius + 1) + dx + Radius] = new Vector<double>(lanes);
            }
        }
    }

    static Vector<double> Sign(Vector<double> x, Vector<double> q) =>
        Vector.ConditionalSelect(Vector.GreaterThan(x, q), Vector<double>.One, Vector<double>.Zero)
        - Vector.ConditionalSelect(Vector.LessThan(x, q), Vector<double>.One, Vector<double>.Zero);

    public void Update(ReadOnlySpan<double> amplitude)
    {
        if (amplitude.Length != Bands) throw new ArgumentException("Spectrum dimensions do not match.", nameof(amplitude));
        for (var k = 0; k < Bands; ++k)
            Magnitude[Radius + k] = double.IsFinite(amplitude[k]) && amplitude[k] > 0 ? amplitude[k] : 0;
        var step = new Vector<double>(Step);
        for (var g = 0; g < Groups; ++g)
        {
            var q = Joint[g];
            var signs = Vector<double>.Zero;
            var above = new Vector<double>(double.PositiveInfinity);
            var below = above;
            for (var dx = -Radius; dx <= Radius; ++dx)
            {
                var x = new Vector<double>(Magnitude, Radius + g * Vector<double>.Count + dx);
                var mask = Masks[g * (2 * Radius + 1) + dx + Radius];
                signs += Sign(x, q) * mask;
                var valid = Vector.GreaterThan(mask, Vector<double>.Zero);
                above = Vector.Min(above, Vector.ConditionalSelect(valid & Vector.GreaterThan(x, q),
                    x - q, new Vector<double>(double.PositiveInfinity)));
                below = Vector.Min(below, Vector.ConditionalSelect(valid & Vector.LessThan(x, q),
                    q - x, new Vector<double>(double.PositiveInfinity)));
            }
            // Preserve the average-sign step, but land on an observation rather
            // than jumping over it. This removes fixed-step chatter in silence.
            var update = step * signs * InverseWidths[g];
            update = Vector.Min(above, Vector.Max(-below, update));
            Joint[g] = Vector.Max(Vector<double>.Zero, q + update);
        }
        for (var g = 0; g < Groups; ++g)
        {
            var current = new Vector<double>(Magnitude, Radius + g * Vector<double>.Count);
            var deviation = SubtractBackground ? current - Joint[g] : current;
            deviation.CopyTo(Directional, Radius + g * Vector<double>.Count);
            // Signed state: deficits must survive until the final display projection.
            Time[g] += Vector.Min(step, Vector.Max(-step, deviation - Time[g]));
        }
    }

    public void Output(Span<double> output)
    {
        if (output.Length < Splitter.OutputStride * Bands) throw new ArgumentException("Output is too short.", nameof(output));
        for (var k = 0; k < Bands; ++k)
        {
            var first = Math.Max(0, k - Radius);
            var width = Math.Min(Bands - 1, k + Radius) - first + 1;
            Directional.AsSpan(Radius + first, width).CopyTo(Scratch);
            var offset = Splitter.OutputStride * k;
            Presentation.Write(Time[k / Vector<double>.Count][k % Vector<double>.Count],
                Splitter.Median(Scratch.AsSpan(0, width)),
                Joint[k / Vector<double>.Count][k % Vector<double>.Count],
                Magnitude[Radius + k], output.Slice(offset, Splitter.OutputStride));
        }
    }
}

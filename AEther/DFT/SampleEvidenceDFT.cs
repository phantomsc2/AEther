using System;
using System.Numerics;

namespace AEther;

/// <summary>Cross-bin SIMD with shared audio history. Produces chronological
/// sample spectra in short tiles, without log conversion or per-sample events.</summary>
internal sealed class SampleEvidenceDFT
{
    const int TileSize = 32;
    readonly int Bands, Groups, Components;
    readonly int[] Lengths;
    readonly double[] History, Tile, Outgoing;
    readonly Vector<double>[] Real, Imaginary, CoefficientReal, CoefficientImaginary, Scale;
    readonly double[] Weights;
    readonly DFTProcessor? Scalar;
    int Position;

    public SampleEvidenceDFT(Domain domain, double sampleRate, AnalyzerOptions options)
    {
        Bands = domain.Length;
        Groups = (Bands + Vector<double>.Count - 1) / Vector<double>.Count;
        Weights = DFTProcessor.CreateWindow(DFTProcessor.GetWindowCosines(options.WindowFunction));
        Components = Weights.Length;
        Lengths = new int[Groups * Vector<double>.Count];
        Real = new Vector<double>[Groups * Components];
        Imaginary = new Vector<double>[Real.Length];
        CoefficientReal = new Vector<double>[Real.Length];
        CoefficientImaginary = new Vector<double>[Real.Length];
        Scale = new Vector<double>[Groups];
        Outgoing = new double[Vector<double>.Count];
        Tile = new double[TileSize * Bands];
        var m = (int)Math.Round(1 / (Math.Pow(2, 1.0 / domain.Resolution) - 1));
        var frequencies = new double[Bands];
        var index = 0;
        foreach (var frequency in domain) frequencies[index++] = frequency;
        var maximum = 1;
        for (var k = 0; k < Bands; ++k)
        {
            Lengths[k] = (int)Math.Round(m / (frequencies[k] / sampleRate));
            maximum = Math.Max(maximum, Lengths[k]);
        }
        History = new double[maximum];
        var re = new double[Vector<double>.Count];
        var im = new double[Vector<double>.Count];
        for (var g = 0; g < Groups; ++g)
        {
            for (var lane = 0; lane < re.Length; ++lane)
            {
                var k = g * re.Length + lane;
                re[lane] = k < Bands ? 2.0 / (Lengths[k] * Weights[Components / 2]) : 0;
            }
            Scale[g] = new Vector<double>(re);
            for (var j = 0; j < Components; ++j)
            {
                Array.Clear(re); Array.Clear(im);
                for (var lane = 0; lane < re.Length; ++lane)
                {
                    var k = g * re.Length + lane;
                    if (k >= Bands) break;
                    var coefficient = Complex.Exp(2 * Math.PI * Complex.ImaginaryOne
                        * (m - Components / 2 + j) / Lengths[k]);
                    re[lane] = coefficient.Real; im[lane] = coefficient.Imaginary;
                }
                CoefficientReal[g * Components + j] = new Vector<double>(re);
                CoefficientImaginary[g * Components + j] = new Vector<double>(im);
            }
        }
        if (!options.SIMDEnabled || !Vector.IsHardwareAccelerated)
            Scalar = new DFTProcessor(domain, sampleRate, false, options.WindowFunction);
    }

    public void Process(ReadOnlySpan<double> samples, SampleSignEvidence evidence)
    {
        while (!samples.IsEmpty)
        {
            var count = Math.Min(TileSize, samples.Length);
            var source = samples[..count];
            if (Scalar is not null)
            {
                for (var n = 0; n < count; ++n)
                    Scalar.ProcessSampleLinear(source[n], Tile.AsSpan(n * Bands, Bands));
            }
            else
            {
                // Keep each group's recurrence near its outgoing reads. History is
                // overwritten only after all groups have consumed this tile.
                for (var g = 0; g < Groups; ++g)
                for (var n = 0; n < count; ++n)
                {
                    for (var lane = 0; lane < Outgoing.Length; ++lane)
                    {
                        var k = g * Outgoing.Length + lane;
                        if (k >= Bands) { Outgoing[lane] = 0; continue; }
                        var age = n - Lengths[k];
                        var oldIndex = Position + age;
                        if (oldIndex < 0) oldIndex += History.Length;
                        Outgoing[lane] = age >= 0 ? source[age] : History[oldIndex];
                    }
                    var delta = new Vector<double>(source[n]) - new Vector<double>(Outgoing);
                    var sumReal = Vector<double>.Zero;
                    var sumImaginary = Vector<double>.Zero;
                    for (var j = 0; j < Components; ++j)
                    {
                        var c = g * Components + j;
                        var re = Real[c]; var im = Imaginary[c];
                        var next = CoefficientReal[c] * re - CoefficientImaginary[c] * im + delta;
                        im = CoefficientReal[c] * im + CoefficientImaginary[c] * re;
                        Real[c] = next; Imaginary[c] = im;
                        sumReal += next * new Vector<double>(Weights[j]);
                        sumImaginary += im * new Vector<double>(Weights[j]);
                    }
                    // Scaled first to avoid overflow in the sum of squares.
                    sumReal *= Scale[g]; sumImaginary *= Scale[g];
                    var amplitude = Vector.SquareRoot(sumReal * sumReal + sumImaginary * sumImaginary);
                    var active = Math.Min(Vector<double>.Count, Bands - g * Vector<double>.Count);
                    for (var lane = 0; lane < active; ++lane)
                        Tile[n * Bands + g * Vector<double>.Count + lane] = amplitude[lane];
                }
                foreach (var sample in source)
                {
                    History[Position] = sample;
                    if (++Position == History.Length) Position = 0;
                }
            }
            for (var n = 0; n < count; ++n)
                evidence.Update(Tile.AsSpan(n * Bands, Bands));
            samples = samples[count..];
        }
    }
}

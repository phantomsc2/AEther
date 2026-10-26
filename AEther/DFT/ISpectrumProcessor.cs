using System;
using System.Numerics;

namespace AEther;

/// <summary>Causal spectrum in coherent linear amplitude or log10 amplitude units.</summary>
public interface ISpectrumProcessor
{
    void Process(ReadOnlyMemory<double> samples);
    void OutputLinear(Memory<double> destination);
    /// <summary>Coherent complex amplitude, normalized exactly like OutputLinear.</summary>
    void OutputComplex(Memory<Complex> destination);
    /// <summary>Actual carrier centers, including finite-DFT bin quantization.</summary>
    ReadOnlyMemory<double> Frequencies { get; }
    void Output(Memory<double> destination);
    void Reset();
}

public static class SpectrumProcessor
{
    public static ISpectrumProcessor Create(Domain domain, double sampleRate, AnalyzerOptions options) =>
        options.UseSwift
            ? new SwiftProcessor(domain, sampleRate, options.SIMDEnabled,
                widthScale: options.SwiftWidthScale, imbalance: options.SwiftImbalance)
            : new DFTProcessor(domain, sampleRate, options.SIMDEnabled, options.WindowFunction);
}

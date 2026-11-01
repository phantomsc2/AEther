using System;

namespace AEther.Tests;

internal static class SpectrumTestOutput
{
    public static void Log(ISpectrumProcessor processor, Memory<double> destination)
    {
        processor.OutputLinear(destination);
        for (var k = 0; k < processor.Frequencies.Length; ++k)
            destination.Span[k] = Math.Log10(destination.Span[k]);
    }
}

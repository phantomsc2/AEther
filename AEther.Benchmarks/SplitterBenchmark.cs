using System;
using BenchmarkDotNet.Attributes;

namespace AEther.Benchmarks;

/// <summary>Steady-state stereo analysis cost, including the Gaussian STBR fit.</summary>
public class SplitterBenchmark
{
    [Params(8, 16)]
    public int FrequencyResolution { get; set; }

    [Params(.2, .5)]
    public double TimeWindowSeconds { get; set; }

    Splitter[] splitters = null!;
    double[][] input = null!;
    double[] output = null!;
    int frame;

    [GlobalSetup]
    public void Setup()
    {
        var options = new AnalyzerOptions { FrequencyResolution = FrequencyResolution, TimeWindowSeconds = TimeWindowSeconds };
        var domain = options.Domain;
        var rate = 48000.0 / (48000 / options.TimeResolution);
        splitters = [new(domain, options, rate), new(domain, options, rate)];
        input = new double[256][];
        output = new double[Splitter.OutputStride * domain.Length];
        var random = new Random(123);
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = new double[domain.Length];
            for (var k = 0; k < domain.Length; k++) input[i][k] = -4 + 3 * random.NextDouble();
        }
        for (var i = 0; i < 256; i++) StereoFrame();
    }

    [Benchmark]
    public void StereoFrame()
    {
        var spectrum = input[frame++ & 255];
        splitters[0].Process(spectrum, output);
        splitters[1].Process(spectrum, output);
    }
}

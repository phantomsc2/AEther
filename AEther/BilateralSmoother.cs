using System;

namespace AEther;

/// <summary>Nonseparable bilateral average of ungained STN with causal time support.
/// History contains classified input, never recursively filtered output.</summary>
internal sealed class BilateralSmoother
{
    readonly int Bands, TimeRadius, FrequencyRadius;
    readonly double EdgeDb;
    readonly double[][] Values, Logs;
    readonly double[] TimeWeights, FrequencyWeights;
    int Head = -1, Count;

    public BilateralSmoother(int bands, double rate, int resolution, AnalyzerOptions options)
    {
        Bands = bands;
        EdgeDb = options.SmoothingEdgeDb;
        TimeRadius = options.SmoothingAmount == 0 ? 0
            : (int)Math.Min(65534, Math.Ceiling(options.SmoothingAmount * options.MedianLength * rate));
        FrequencyRadius = options.SmoothingAmount == 0 ? 0
            : (int)Math.Min(bands - 1, Math.Ceiling(options.SmoothingAmount * options.MedianWidth * resolution / 2));
        Values = new double[TimeRadius + 1][];
        Logs = new double[Values.Length][];
        for (var i = 0; i < Values.Length; i++)
        {
            Values[i] = new double[3 * bands];
            Logs[i] = new double[3 * bands];
        }
        TimeWeights = SpatialWeights(TimeRadius);
        FrequencyWeights = SpatialWeights(FrequencyRadius);
    }

    static double[] SpatialWeights(int radius)
    {
        var weights = new double[radius + 1];
        var sigma = Math.Max(.5, radius / 2.0);
        for (var i = 0; i <= radius; i++) weights[i] = Math.Exp(-.5 * Math.Pow(i / sigma, 2));
        return weights;
    }

    public void Process(ReadOnlySpan<double> input, Span<double> output)
    {
        Head = (Head + 1) % Values.Length;
        Count = Math.Min(Count + 1, Values.Length);
        input.CopyTo(Values[Head]);
        for (var i = 0; i < input.Length; i++)
            // Similarity floor only: no amplitude floor is added to the output.
            Logs[Head][i] = 20 * Math.Log10(Math.Max(1e-300, input[i]));
        if (TimeRadius == 0 && FrequencyRadius == 0)
        {
            input.CopyTo(output);
            return;
        }
        for (var k = 0; k < Bands; k++)
            for (var c = 0; c < 3; c++)
            {
                var index = 3 * k + c;
                double average = input[index], weightSum = 1;
                var centerLog = Logs[Head][index];
                for (var age = 0; age < Count; age++)
                {
                    var row = (Head - age + Values.Length) % Values.Length;
                    for (var q = Math.Max(0, k - FrequencyRadius); q <= Math.Min(Bands - 1, k + FrequencyRadius); q++)
                    {
                        if (age == 0 && q == k) continue;
                        var j = 3 * q + c;
                        var delta = (Logs[row][j] - centerLog) / EdgeDb;
                        var weight = TimeWeights[age] * FrequencyWeights[Math.Abs(q - k)] * Math.Exp(-.5 * delta * delta);
                        var nextWeight = weightSum + weight;
                        // Incremental convex mean avoids overflow in a sum of magnitudes.
                        average += (Values[row][j] - average) * (weight / nextWeight);
                        weightSum = nextWeight;
                    }
                }
                output[index] = average;
            }
    }
}

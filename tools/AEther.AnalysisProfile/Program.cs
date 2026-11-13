using System.Globalization;
using System.Runtime.InteropServices;
using AEther;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
const int warmup = 4000, retained = 6000;
var folder = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/analysis-profile/smoother");
Directory.CreateDirectory(folder);
using var csv = new StreamWriter(Path.Combine(folder, "results.csv"));
csv.WriteLine("round,backend,bins,mode,stage,count,mean_us,median_us,p95_us,sum_ms");
Console.WriteLine($"{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}; {RuntimeInformation.FrameworkDescription}; 48 kHz stereo; 1000 ticks/s; {warmup} warmup + {retained} retained ticks per run.");
// Precompute identical non-silent input outside timing. Exercise sustained tones,
// transients and broadband noise rather than exact-zero reset/initialization paths.
var audio = new float[(warmup + retained) * 48 * 2];
var random = new Random(123);
for (int i = 0; i < audio.Length / 2; i++)
{
    double t = i / 48000.0;
    double pulse = Math.Exp(-(t % .4) * 40);
    audio[2 * i] = (float)(.025 * (Math.Sin(t * Math.Tau * 220) + Math.Sin(t * Math.Tau * 440))
        + .04 * pulse * Math.Sin(t * Math.Tau * 80) + .002 * (random.NextDouble() - .5));
    audio[2 * i + 1] = (float)(.025 * (Math.Sin(t * Math.Tau * 330) + Math.Sin(t * Math.Tau * 660))
        + .04 * pulse * Math.Sin(t * Math.Tau * 120) + .002 * (random.NextDouble() - .5));
}
var bytes = MemoryMarshal.AsBytes(audio.AsSpan()).ToArray();
string[] stages = ["analysis.dft_channel_ms", "analysis.smooth_channel_ms", "analysis.split_channel_ms",
    "analysis.processing_ms", "analysis.total_ms"];
var cases = (from setup in new[] { (Swift: true, Resolution: 12), (Swift: true, Resolution: 48), (Swift: false, Resolution: 12) }
             from mode in new[] { "Off", "LogAmplitude", "Complex" }
             select (setup.Swift, setup.Resolution, Mode: mode)).ToArray();
for (int round = 0; round < 3; round++)
foreach (var test in cases.Skip(round * 3).Concat(cases.Take(round * 3)))
{
    var options = new AnalyzerOptions
    {
        UseSwift = test.Swift, FrequencyResolution = test.Resolution,
        SmoothingEnabled = test.Mode != "Off",
        SmoothingMode = test.Mode == "Complex" ? SmoothingMode.Complex : SmoothingMode.LogAmplitude,
    };
    var analyzer = new Analyzer(new(SampleType.Float32.Instance, 48000, 2), options);
    var histories = stages.ToDictionary(s => s, s => analyzer.Profile.Track(s, 32768));
    var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int count = 0;
    analyzer.SamplesAnalyzed += (_, _) => { if (++count == warmup + retained) complete.SetResult(); };
    try
    {
        analyzer.Start();
        // Untimestamped offline input: no pacing, expiry, capture I/O or rendering.
        analyzer.PostSamples(bytes);
        await complete.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
    finally { analyzer.Stop(); await analyzer.Completion; }
    if (analyzer.DroppedInputCount != 0 || count != warmup + retained) throw new Exception("Input lost.");
    var sums = new Dictionary<string, double>();
    foreach (var stage in stages)
    {
        var samples = new List<TimingSample>();
        long cursor = 0;
        if (histories[stage].ReadSince(ref cursor, samples) != 0) throw new Exception("Timing history overflow.");
        bool perChannel = stage.Contains("channel");
        var values = samples.Skip(warmup * (perChannel ? 2 : 1)).Select(s => s.Milliseconds).Order().ToArray();
        if (values.Length != retained * (perChannel ? 2 : 1)) throw new Exception($"Missing timings: {stage}");
        sums[stage] = values.Sum();
        csv.WriteLine($"{round},{(test.Swift ? "SWIFT" : "DFT")},{analyzer.Domain.Length},{test.Mode},{stage},{values.Length},{values.Average() * 1000:F4},{values[values.Length / 2] * 1000:F4},{values[(int)(values.Length * .95)] * 1000:F4},{values.Sum():F4}");
    }
    csv.Flush();
    Console.WriteLine($"Round {round}: {(test.Swift ? "SWIFT" : "DFT"),5} {analyzer.Domain.Length} bins {test.Mode,-12}: processing {sums["analysis.processing_ms"] / retained * 1000:F2} us/stereo tick; smoother {sums["analysis.smooth_channel_ms"] / sums["analysis.processing_ms"] * 100:F1}%");
}
Console.WriteLine(folder);

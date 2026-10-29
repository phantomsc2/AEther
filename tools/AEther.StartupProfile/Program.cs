using System.Diagnostics;
using System.Globalization;
using AEther;
using AEther.Vortice;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var output = args.FirstOrDefault() ?? "artifacts/startup-profile.csv";
        var runs = args.Length > 1 ? int.Parse(args[1]) : 11;
        if (runs < 2) throw new ArgumentOutOfRangeException(nameof(runs));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var csv = new StreamWriter(output);
        csv.WriteLine("backend,scene,run,stage,milliseconds");
        using var window = new Form { ClientSize = new(1280, 720), ShowInTaskbar = false };
        // Create the HWND before timing. No capture device or WinUI dispatcher is involved.
        var handle = window.Handle;
        var format = new SampleFormat(SampleType.Float32.Instance, 48000, 2);
        string[] stages = ["startup.analysis_ms", "startup.dft_ms", "startup.smoother_ms", "startup.splitter_ms",
            "startup.gpu_ms", "startup.gpu_device_ms", "startup.gpu_output_ms", "startup.gpu_shaders_ms",
            "startup.first_draw_ms", "startup.first_frame_ready_ms", "startup.dispose_ms", "startup.total_ms"];
        Console.WriteLine($"{Environment.OSVersion}; {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; 1280x720; 48 kHz stereo; {runs} runs/case");
        foreach (bool swift in new[] { true, false })
        foreach (string scene in new[] { "MandelPond", "MandelFluid" })
        {
            var results = stages.ToDictionary(s => s, _ => new List<double>());
            for (int run = 0; run < runs; run++)
            {
                var total = Stopwatch.GetTimestamp();
                var options = new AnalyzerOptions { UseSwift = swift };
                var analyzer = new Analyzer(format, options);
                var profile = analyzer.Profile;
                try
                {
                    var rate = format.SampleRate / (double)(format.SampleRate / options.TimeResolution);
                    var renderer = new Renderer(handle, 1280, 720, options.Domain.Length, rate,
                        initialScene: scene, profile: profile);
                    try
                    {
                        if (run == 0) Console.WriteLine($"{(swift ? "SWIFT" : "DFT")} / {scene}: {renderer.AdapterDescription}; {renderer.OutputDescription}; {options.Domain.Length} bins");
                        // Includes lazy scene resources, initial history upload and submission.
                        using (profile.Measure("startup.first_draw_ms")) renderer.Render(false);
                        // Readback synchronizes GPU completion, unlike Render's CPU wall time.
                        renderer.ReadPixels();
                        profile.Elapsed("startup.first_frame_ready_ms", total);
                    }
                    finally
                    {
                        using (profile.Measure("startup.dispose_ms"))
                        {
                            analyzer.Stop();
                            analyzer.Completion.GetAwaiter().GetResult();
                            renderer.Dispose();
                        }
                    }
                }
                finally { analyzer.Stop(); analyzer.Completion.GetAwaiter().GetResult(); }
                profile.Elapsed("startup.total_ms", total);
                foreach (var stage in stages)
                {
                    double ms = profile.Recent(stage)!.Value.Mean;
                    results[stage].Add(ms);
                    csv.WriteLine($"{(swift ? "SWIFT" : "DFT")},{scene},{run},{stage},{ms:F6}");
                }
                csv.Flush();
            }
            foreach (var stage in stages)
            {
                var warm = results[stage].Skip(1).Order().ToArray();
                Console.WriteLine($"  {stage,-34} first {results[stage][0],9:F3} ms; warm median {warm[warm.Length / 2],9:F3}; p95 {warm[(int)Math.Ceiling(.95 * (warm.Length - 1))],9:F3}");
            }
        }
        Console.WriteLine(Path.GetFullPath(output));
        return 0;
    }
}

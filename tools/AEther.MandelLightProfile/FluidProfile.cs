using System.Diagnostics;
using System.Text.Json;
using AEther;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunFluidProfile(string[] args)
    {
        string Option(string key, string fallback) => args.FirstOrDefault(a => a.StartsWith(key + "="))?[(key.Length + 1)..] ?? fallback;
        string source = Path.GetFullPath(Option("--shader-root", "AEther.Vortice/Shaders"));
        string output = Path.GetFullPath(Option("--output", "artifacts/fluid/current"));
        int count = int.Parse(Option("--frames", "48"), System.Globalization.CultureInfo.InvariantCulture);
        int rate = int.Parse(Option("--hz", "60"));
        int width = int.Parse(Option("--width", "1280")), height = int.Parse(Option("--height", "720"));
        if (width < 1280 || height < 720 || rate < 1 || count < 1) throw new ArgumentOutOfRangeException(nameof(args));
        Directory.CreateDirectory(output);
        string tree = Path.Combine(output, "Shaders");
        Directory.CreateDirectory(Path.Combine(tree, "Scenes"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "Common"), "*.hlsl", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(tree, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        File.Copy(Path.Combine(source, "Scenes/MandelFluid.hlsl"), Path.Combine(tree, "Scenes/MandelFluid.hlsl"), true);
        var results = new List<object>();
        foreach (var item in new[] { (Name: "default", Low: false, Periodic: true, Viscosity: 0f),
            (Name: "viscous", Low: false, Periodic: true, Viscosity: .5f),
            (Name: "walls", Low: false, Periodic: false, Viscosity: 0f),
            (Name: "low", Low: true, Periodic: true, Viscosity: 0f) })
        {
            if (Option("--case", "all") is var selected && selected != "all" && selected != item.Name) continue;
            using var window = new Form { ClientSize = new(width, height), ShowInTaskbar = false };
            var clock = new FluidProfileClock(rate);
            using var renderer = new Renderer(window.Handle, width, height, 97, 1000, shaderDirectory: tree,
                timeProvider: clock);
            renderer.SelectScene("MandelFluid");
            Set(renderer, "Fluid.LowQuality", item.Low);
            Set(renderer, "Fluid.PeriodicBoundary", item.Periodic);
            Set(renderer, "Fluid.Viscosity", item.Viscosity);
            if (args.Contains("--filaments"))
            {
                Set(renderer, "Fluid.VelocityGain", .5f);
                Set(renderer, "Fluid.Curl", .25f);
            }
            if (args.Contains("--fast"))
            {
                Set(renderer, "Fluid.EmissionVelocity", 1f);
                Set(renderer, "Fluid.VelocityGain", .75f);
                Set(renderer, "Fluid.Curl", .75f);
            }
            var device = Field<ID3D11Device>(renderer, "device");
            var context = Field<ID3D11DeviceContext>(renderer, "context");
            using var timer = new Timestamps(device, context);
            long nextRow = 0;
            int frame = 0;
            Dictionary<string, double> Tick()
            {
                long end = ++frame * 1000L / rate;
                while (nextRow < end)
                {
                    var values = new double[97 * 8];
                    for (int channel = 0; channel < 2; channel++)
                    for (int band = 0; band < 97; band++)
                    {
                        double wave = .5 + .5 * Math.Sin(band * .29 + nextRow * .004 + channel * .6);
                        int at = (channel * 97 + band) * 4;
                        values[at] = .08 * wave;
                        values[at + 1] = nextRow % 400 < 30 ? .12 * wave : 0;
                        values[at + 2] = .02 + .06 * wave;
                        values[at + 3] = .2;
                    }
                    renderer.PostAudio(new(values, 97 * 4, DateTime.UnixEpoch) { AnalysisRow = nextRow++ }, 2);
                }
                clock.Advance();
                timer.Reset(); renderer.TransportPassMarker = timer.Marker; timer.Begin();
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                var start = Stopwatch.GetTimestamp();
                renderer.Render(false);
                double cpu = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
                timer.End(); renderer.TransportPassMarker = null;
                var result = timer.Resolve();
                result["CPU submission"] = cpu; result["Allocated bytes"] = allocated;
                result["Simulation steps"] = renderer.Fluid.LastSteps;
                result["Simulation draws"] = renderer.Fluid.LastDraws;
                return result;
            }
            var startup = Tick();
            for (int warm = 0; warm < 24; warm++) Tick();
            var samples = Enumerable.Range(0, count).Select(_ => Tick()).ToArray();
            var summaries = samples.SelectMany(f => f.Keys).Distinct().ToDictionary(k => k,
                k => new { Median = Quantile(samples.Select(f => f.GetValueOrDefault(k)), .5), P95 = Quantile(samples.Select(f => f.GetValueOrDefault(k)), .95) });
            Console.WriteLine($"{item.Name}, {renderer.AdapterDescription}, {width}x{height}, {rate} Hz, {renderer.FluidWork}: " +
                string.Join(", ", summaries.Select(p => $"{p.Key}={p.Value.Median:F3}/{p.Value.P95:F3}")));
            var pixels = renderer.ReadPixels(false);
            File.WriteAllBytes(Path.Combine(output, item.Name + ".rgba"), pixels);
            Save(pixels, width, height, Path.Combine(output, item.Name + ".png"));
            var projection = renderer.Fluid.CheckProjection(renderer.FluidPrograms);
            results.Add(new { item.Name, Adapter = renderer.AdapterDescription, Width = width, Height = height, Rate = rate,
                Frames = count, Startup = startup, Timing = summaries, Samples = samples,
                ProjectionBefore = projection.Before, ProjectionAfter = projection.After, projection.CurlError });
            File.WriteAllText(Path.Combine(output, "profile.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        static double Quantile(IEnumerable<double> values, double q)
        {
            var sorted = values.Order().ToArray(); return sorted[(int)Math.Ceiling(q * (sorted.Length - 1))];
        }
    }

    sealed class FluidProfileClock(int rate) : TimeProvider
    {
        long frame;
        public override long TimestampFrequency => rate;
        public override long GetTimestamp() => frame;
        public void Advance() => frame++;
    }
}

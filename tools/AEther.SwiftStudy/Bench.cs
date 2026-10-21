using System.Diagnostics;

namespace AEther.SwiftStudy;

static class Bench
{
    public static void Run(string directory)
    {
        using var csv = new StreamWriter(Path.Combine(directory, "performance.csv")) { AutoFlush = true };
        csv.WriteLine("sample_rate,bins_per_octave,bands,block,workload,backend,mean_us,median_us,p95_us,max_us,min_round_mean_us,max_round_mean_us,allocated_bytes_per_call,construction_bytes_stereo,iterations");
        foreach (int fs in new[] { 44100, 48000 })
        foreach (int resolution in new[] { 12, 24 })
        foreach (int block in new[] { fs / 1000, 187, 1024 })
        foreach (bool withFit in new[] { false, true })
        foreach (var name in new[] { "Hann SIMD", "SWIFT scalar", "SWIFT SIMD" })
        {
            var options = new AnalyzerOptions { FrequencyResolution = resolution, InputGainDb = 0 };
            var domain = options.Domain;
            // Force one-time runtime initialization before measuring construction allocations.
            Quality.Create(name, new Domain(440, 12, 1), fs);
            long beforeSetup = GC.GetAllocatedBytesForCurrentThread();
            var banks = new[] { Quality.Create(name, domain, fs), Quality.Create(name, domain, fs) };
            long construction = GC.GetAllocatedBytesForCurrentThread() - beforeSetup;
            double rate = fs / (double)block;
            var splits = new[] { new Splitter(domain, options, rate), new Splitter(domain, options, rate) };
            var global = new GlobalSplitter(domain, options, rate);
            var random = new Random(123);
            var input = Enumerable.Range(0, 64).Select(_ => Enumerable.Range(0, block).Select(_ => .3 * (random.NextDouble() - .5)).ToArray()).ToArray();
            var magnitudes = new double[domain.Length * 2]; var output = new double[domain.Length * 4];
            int frame = 0;
            void Step()
            {
                for (int c = 0; c < 2; ++c)
                {
                    banks[c].Process(input[(frame + c * 17) & 63]);
                    banks[c].OutputLinear(magnitudes.AsMemory(c * domain.Length, domain.Length));
                    if (withFit) splits[c].ProcessLinear(magnitudes.AsMemory(c * domain.Length, domain.Length), output);
                }
                if (withFit) global.Process(magnitudes, 2);
                frame++;
            }
            var clock = Stopwatch.StartNew();
            int warmFrames = (int)Math.Ceiling(17.0 * fs / 30 / block) + (int)Math.Ceiling(.2 * rate);
            while (clock.Elapsed.TotalMilliseconds < 200 || frame < warmFrames) Step();
            clock.Restart(); for (int i = 0; i < 100; ++i) Step();
            int iterations = Math.Clamp((int)(200 / Math.Max(.001, clock.Elapsed.TotalMilliseconds / 100)), 100, 25000);
            var elapsed = new double[5 * iterations]; var roundMeans = new double[5];
            long allocated = 0;
            for (int round = 0; round < 5; ++round)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < iterations; ++i)
                {
                    long start = Stopwatch.GetTimestamp(); Step();
                    elapsed[round * iterations + i] = (Stopwatch.GetTimestamp() - start) * 1e6 / Stopwatch.Frequency;
                }
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                roundMeans[round] = elapsed.AsSpan(round * iterations, iterations).ToArray().Average();
            }
            double mean = elapsed.Average(); Array.Sort(elapsed);
            double median = elapsed[elapsed.Length / 2], p95 = elapsed[(int)(.95 * (elapsed.Length - 1))];
            string workload = withFit ? "spectrum+local+global" : "spectrum";
            csv.WriteLine($"{fs},{resolution},{domain.Length},{block},{workload},{name},{mean:G17},{median:G17},{p95:G17},{elapsed[^1]:G17},"
                + $"{roundMeans.Min():G17},{roundMeans.Max():G17},{allocated / (double)elapsed.Length:G17},{construction},{elapsed.Length}");
            Console.WriteLine($"{fs} Hz / {resolution} bins/oct / {block} samples / {workload} / {name}: median {median:F2}, p95 {p95:F2} us stereo, alloc {allocated / (double)elapsed.Length:G3}");
        }
    }
}

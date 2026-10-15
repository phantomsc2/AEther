using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace AEther;

/// <summary>Opt-in CPU/wait timings. Does not measure GPU execution or audio-to-photon latency.</summary>
public sealed class PipelineProfile
{
    readonly ConcurrentQueue<(double Time, string Stage, double Milliseconds)> Rows = new();
    volatile bool enabled;
    long origin;
    int count;
    readonly ConcurrentDictionary<string, string> Metadata = new();
    readonly ConcurrentDictionary<string, Queue<double>> live = new();

    public (double Mean, double P95)? Recent(string stage)
    {
        if (!live.TryGetValue(stage, out var values)) return null;
        double[] snapshot;
        lock (values) snapshot = values.ToArray();
        if (snapshot.Length == 0) return null;
        Array.Sort(snapshot);
        return (snapshot.Average(), snapshot[(int)Math.Ceiling(.95 * (snapshot.Length - 1))]);
    }
    public bool Enabled => enabled;

    public void Describe(string key, string value) => Metadata[key] = value;

    public void Start()
    {
        enabled = false;
        Rows.Clear();
        count = 0;
        origin = Stopwatch.GetTimestamp();
        enabled = true;
    }

    public void Record(string stage, double milliseconds)
    {
        var values = live.GetOrAdd(stage, _ => new Queue<double>(256));
        lock (values)
        {
            if (values.Count == 256) values.Dequeue();
            values.Enqueue(milliseconds);
        }
        if (enabled && Interlocked.Increment(ref count) <= 200000)
            Rows.Enqueue((Stopwatch.GetElapsedTime(origin).TotalSeconds, stage, milliseconds));
    }

    public void Elapsed(string stage, long start)
    {
        Record(stage, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    public Scope Measure(string stage) => new(this, stage, Stopwatch.GetTimestamp());
    public readonly struct Scope(PipelineProfile profile, string stage, long start) : IDisposable
    {
        public void Dispose() { if (start != 0) profile.Elapsed(stage, start); }
    }

    public string Save(string folder)
    {
        enabled = false;
        var snapshot = Rows.ToArray();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"pipeline-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv");
        var ci = CultureInfo.InvariantCulture;
        File.WriteAllLines(path, new[] { "elapsed_seconds,stage,milliseconds" }.Concat(snapshot.Select(r =>
            $"{r.Time.ToString("F6", ci)},{r.Stage},{r.Milliseconds.ToString("F6", ci)}")));
        File.WriteAllLines(Path.ChangeExtension(path, ".txt"), Metadata.OrderBy(pair => pair.Key)
            .Select(pair => $"{pair.Key}: {pair.Value}").Concat(new[] {
            "CPU/wait timings in milliseconds; stages can overlap and must not all be added.",
            "DFT and split rows are per channel. Capture/completion/render intervals use the monotonic clock.",
            "Present measures CPU blocking, not GPU duration or scanout. First frames include warmup.",
            "stage,count,median,p95,p99,max" }).Concat(snapshot.GroupBy(r => r.Stage).Select(group => {
                var values = group.Select(r => r.Milliseconds).Order().ToArray();
                double Q(double q) => values[(int)Math.Ceiling(q * (values.Length - 1))];
                return $"{group.Key},{values.Length},{Q(.5).ToString("F3", ci)},{Q(.95).ToString("F3", ci)},{Q(.99).ToString("F3", ci)},{values[^1].ToString("F3", ci)}";
            })));
        return path;
    }
}


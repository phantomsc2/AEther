using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AEther;

// Captures timing/size metadata only; audio samples are never saved.
var lines = new List<string> { "mode,count,packet_median_ms,packet_p95_ms,interval_median_ms,interval_p95_ms,callback_mean_us" };
foreach (var request in new[] { 1, 2, 5, 10 })
{
    var mode = $"NAudio-request-{request}ms";
    try
    {
        SampleSource source = new AEther.NAudio.Loopback(request);
        var packets = new List<double>(); var intervals = new List<double>();
        long previous = 0; double callbackMicroseconds = 0;
        source.DataAvailable += (_, data) =>
        {
            if (data.IsEmpty) return;
            var now = Stopwatch.GetTimestamp();
            lock (packets)
            {
                packets.Add(data.Length * 1000.0 / source.Format.Size / source.Format.SampleRate);
                if (previous != 0) intervals.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                previous = now;
                callbackMicroseconds += Stopwatch.GetElapsedTime(now).TotalMicroseconds;
            }
        };
        source.Stopped += (_, error) => { if (error != null) Console.WriteLine($"{mode}: {error}"); };
        try
        {
            source.Start();
            Thread.Sleep(3000);
            source.Stop();
        }
        finally { source.Dispose(); }
        double Q(List<double> values, double q) => values.Count == 0 ? double.NaN : values.Order().ElementAt((int)Math.Ceiling(q * (values.Count - 1)));
        var line = string.Join(",", new[] { mode, packets.Count.ToString(), Q(packets, .5).ToString("F3", CultureInfo.InvariantCulture),
            Q(packets, .95).ToString("F3", CultureInfo.InvariantCulture), Q(intervals, .5).ToString("F3", CultureInfo.InvariantCulture),
            Q(intervals, .95).ToString("F3", CultureInfo.InvariantCulture), (callbackMicroseconds / Math.Max(1, packets.Count)).ToString("F3", CultureInfo.InvariantCulture) });
        lines.Add(line); Console.WriteLine(line);
    }
    catch (Exception error) { Console.WriteLine($"{mode}: {error.Message}"); }
}
var output = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? "capture-probe.csv");
File.WriteAllLines(output, lines);
Console.WriteLine($"Saved timing metadata: {output}");

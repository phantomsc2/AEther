using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AEther;

// Captures timing/size metadata only; audio samples are never saved.
var lines = new List<string> { "mode,count,packet_median_ms,packet_p95_ms,interval_median_ms,interval_p95_ms,callback_mean_us,timestamped_packets,timestamp_errors,discontinuities,source_end_age_median_ms,source_end_age_p95_ms" };
foreach (var request in new[] { 1, 2, 5, 10 })
{
    var mode = $"NAudio-request-{request}ms";
    try
    {
        var source = new AEther.NAudio.Loopback(request);
        var packets = new List<double>(); var intervals = new List<double>();
        var sourceAges = new List<double>();
        int timestampErrors = 0, discontinuities = 0;
        long previous = 0; double callbackMicroseconds = 0;
        source.TimedDataAvailable += (_, data, timing) =>
        {
            if (data.IsEmpty) return;
            var now = Stopwatch.GetTimestamp();
            lock (packets)
            {
                packets.Add(data.Length * 1000.0 / source.Format.Size / source.Format.SampleRate);
                if (previous != 0) intervals.Add(Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                previous = now;
                if (timing.Timestamp.HasValue)
                    sourceAges.Add(AudioClock.Seconds(now - timing.Timestamp.Value
                        - AudioClock.Frames(data.Length / source.Format.Size, source.Format.SampleRate)) * 1000);
                if (timing.Flags.HasFlag(AudioPacketFlags.TimestampError)) timestampErrors++;
                if (timing.Flags.HasFlag(AudioPacketFlags.Discontinuity)) discontinuities++;
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
            Q(intervals, .95).ToString("F3", CultureInfo.InvariantCulture), (callbackMicroseconds / Math.Max(1, packets.Count)).ToString("F3", CultureInfo.InvariantCulture),
            sourceAges.Count.ToString(), timestampErrors.ToString(), discontinuities.ToString(),
            Q(sourceAges, .5).ToString("F3", CultureInfo.InvariantCulture), Q(sourceAges, .95).ToString("F3", CultureInfo.InvariantCulture) });
        lines.Add(line); Console.WriteLine(line);
    }
    catch (Exception error) { Console.WriteLine($"{mode}: {error.Message}"); }
}
var output = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--")) ?? "capture-probe.csv");
File.WriteAllLines(output, lines);
Console.WriteLine($"Saved timing metadata: {output}");

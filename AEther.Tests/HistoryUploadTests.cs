using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture]
public class HistoryUploadTests
{
    [Test, Category("GPU"), NonParallelizable, Apartment(ApartmentState.STA)]
    public void DelayedGpuResultsKeepTheOriginalFrameTimestamp()
    {
        using var fixture = new Fixture();
        using var renderer = fixture.Renderer(1, 100, .04);
        var profile = new PipelineProfile();
        renderer.Audio.Profile = profile;
        var intervals = profile.Track("render.frame_interval_ms");
        var durations = profile.Track("gpu.frame_ms");
        for (int i = 0; i < 12; i++)
        {
            renderer.Render(false);
            renderer.ReadHistogram(0); // Complete the frame; query delivery is deliberately later.
        }
        renderer.PollGpuTimings();
        var cpu = new List<TimingSample>();
        var gpu = new List<TimingSample>();
        long cpuCursor = 0, gpuCursor = 0;
        intervals.ReadSince(ref cpuCursor, cpu);
        durations.ReadSince(ref gpuCursor, gpu);
        Assert.That(gpu, Has.Count.EqualTo(12));
        Assert.That(cpu, Has.Count.EqualTo(11)); // The first frame has no preceding interval.
        Assert.That(gpu.Select(s => s.Timestamp).Order().Skip(1), Is.EqualTo(cpu.Select(s => s.Timestamp)));
    }

    [Test]
    public void DirtyRangesAccumulateAndIncludeClearedGaps()
    {
        var history = new AudioHistory(3, 100, .02); // Four physical rows.
        var mirror = new[] { new float[48], new float[48] };
        Check(new AudioHistory.HistoryRange(0, 4));
        Check();
        Post(0); Check(new AudioHistory.HistoryRange(0, 1));
        Post(1); Post(2); Check(new AudioHistory.HistoryRange(1, 2));
        Post(3); Post(4); Check(new AudioHistory.HistoryRange(3, 1), new AudioHistory.HistoryRange(0, 1));
        Post(7); Check(new AudioHistory.HistoryRange(1, 3)); // The missing rows must reach the GPU as zeros.
        Post(20); Check(new AudioHistory.HistoryRange(0, 4));
        Post(20); Check(); // Duplicate input makes no change.
        Post(0, 1); Check(new AudioHistory.HistoryRange(0, 4)); // Rewinding a generation clears the ring.
        Post(1, 2); Check(new AudioHistory.HistoryRange(1, 1)); // A forward generation preserves old history.
        history.History[1][37] = 9;
        history.InvalidateHistory(); Check(new AudioHistory.HistoryRange(0, 4));
        Post(2); Post(3); Post(4); Post(5); Post(6); Check(new AudioHistory.HistoryRange(0, 4));

        void Post(long row, long generation = 0)
        {
            history.Post(Input(history.Bands, row, generation), 2);
            history.Advance(0);
        }
        void Check(params AudioHistory.HistoryRange[] expected)
        {
            Span<AudioHistory.HistoryRange> ranges = stackalloc AudioHistory.HistoryRange[2];
            int count = history.GetHistoryUpdates(ranges);
            Assert.That(ranges[..count].ToArray(), Is.EqualTo(expected));
            // Peeking does not acknowledge an upload.
            Assert.That(history.GetHistoryUpdates(ranges), Is.EqualTo(count));
            for (int c = 0; c < 2; c++)
            foreach (var range in ranges[..count])
                history.History[c].AsSpan(range.Start * 12, range.Count * 12)
                    .CopyTo(mirror[c].AsSpan(range.Start * 12));
            history.AcknowledgeHistoryUpload();
            Assert.That(mirror, Is.EqualTo(history.History));
        }
    }

    [Test, Category("GPU"), NonParallelizable, Apartment(ApartmentState.STA)]
    public void GpuRingMatchesCpuAndFullUploadsAcrossWrapsAndResets()
    {
        using var fixture = new Fixture();
        using var fullFixture = new Fixture();
        using var incremental = fixture.Renderer(7, 100, .04);
        using var full = fullFixture.Renderer(7, 100, .04);
        incremental.Audio.Profile = new PipelineProfile();
        full.FullHistoryUploads = true;
        Render(incremental.Audio.Rows, 2);
        Render(0, 0);
        Post(0); Render(1, 2);
        Post(1); Post(2); Render(2, 2);
        Post(5); Post(6); Render(4, 4); // Missing rows and physical wrap.
        Post(19); Render(incremental.Audio.Rows, 2);
        Post(19); Render(0, 0);
        Post(0, 1); Render(incremental.Audio.Rows, 2);
        Post(1, 2); Render(1, 2);
        for (int row = 2; row < 16; row++) Post(row, 2);
        Render(incremental.Audio.Rows, 2);
        foreach (var renderer in new[] { incremental, full })
        {
            renderer.Audio.History[0][3] = .75f;
            renderer.Audio.History[1][17] = .625f;
            renderer.Audio.InvalidateHistory();
        }
        Render(incremental.Audio.Rows, 2);
        incremental.Resize(1280, 800); full.Resize(1280, 800);
        Render(0, 0);
        incremental.PollGpuTimings();
        Assert.That(incremental.Audio.Profile.Recent("gpu.frame_ms"), Is.Not.Null);
        Assert.That(incremental.InvalidGpuTimingFrames, Is.Zero);

        void Post(long row, long generation = 0)
        {
            var input = Input(7, row, generation);
            incremental.PostAudio(input, 2); full.PostAudio(input, 2);
        }
        void Render(int rows, int calls)
        {
            incremental.Render(false); full.Render(false);
            Assert.That(incremental.LastHistoryUpload.Rows, Is.EqualTo(rows));
            Assert.That(incremental.LastHistoryUpload.Calls, Is.EqualTo(calls));
            Assert.That(incremental.LastHistoryUpload.Bytes, Is.EqualTo(rows * 7 * 16 * 2));
            for (int c = 0; c < 2; c++)
            {
                Assert.That(incremental.ReadHistogram(c), Is.EqualTo(incremental.Audio.History[c]));
                Assert.That(incremental.ReadHistogram(c), Is.EqualTo(full.ReadHistogram(c)));
            }
            Assert.That(incremental.ReadPixels(false), Is.EqualTo(full.ReadPixels(false)));
        }
    }

    [Test, Category("GPU"), Category("Performance"), NonParallelizable, Apartment(ApartmentState.STA)]
    public void CompareFullAndIncrementalUploadsAt720p()
    {
        using var fixture = new Fixture();
        var timings = new[] { new List<double>(), new List<double>() };
        var frames = new[] { new List<double>(), new List<double>() };
        long[] bytes = new long[2];
        byte[] reference = null;
        for (int round = 0; round < 3; round++)
        foreach (int mode in round % 2 == 0 ? new[] { 0, 1 } : new[] { 1, 0 })
        {
            fixture.Clock.Seconds = 0;
            using var renderer = fixture.Renderer(97, 1000, 2);
            renderer.FullHistoryUploads = mode == 0;
            long nextRow = 0;
            for (int frame = 0; frame < 112; frame++)
            {
                fixture.Clock.Seconds = (frame + 1) / 60.0;
                long endRow = (frame + 1) * 1000 / 60;
                while (nextRow < endRow) renderer.PostAudio(Input(97, nextRow++), 2);
                var start = Stopwatch.GetTimestamp();
                renderer.Render(false);
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                if (frame >= 16)
                {
                    timings[mode].Add(renderer.LastHistoryUpload.Milliseconds);
                    frames[mode].Add(elapsed);
                    bytes[mode] += renderer.LastHistoryUpload.Bytes;
                }
                // Bound outstanding GPU work. This readback is outside CPU timings.
                var pixels = renderer.ReadPixels(false);
                if (frame == 111)
                {
                    if (reference is null) reference = pixels;
                    else Assert.That(pixels, Is.EqualTo(reference));
                }
            }
            TestContext.Out.WriteLine($"Adapter: {renderer.AdapterDescription}");
        }
        for (int mode = 0; mode < 2; mode++)
            TestContext.Out.WriteLine($"{(mode == 0 ? "full" : "incremental")}: 1280x720, " +
                $"{timings[mode].Count} frames; upload CPU median/p95 {Q(timings[mode], .5):F4}/{Q(timings[mode], .95):F4} ms; " +
                $"frame submission CPU median/p95 {Q(frames[mode], .5):F4}/{Q(frames[mode], .95):F4} ms; " +
                $"histogram bytes/frame {bytes[mode] / timings[mode].Count}; final pixels identical.");
        Assert.That(bytes[1], Is.LessThan(bytes[0] / 100));

        static double Q(List<double> values, double fraction) => values.Order().ElementAt((int)Math.Ceiling(fraction * (values.Count - 1)));
    }

    static SampleEvent<double> Input(int bands, long row, long generation = 0)
    {
        var samples = new double[bands * 8];
        for (int i = 0; i < samples.Length; i++) samples[i] = ((row * 13 + i * 7) % 97 + 1) / 128.0;
        return new(samples, bands * 4, DateTime.UnixEpoch) { AnalysisRow = row, DiscontinuityGeneration = generation };
    }

    sealed class Clock : TimeProvider
    {
        public double Seconds;
        public override long TimestampFrequency => 10_000_000;
        public override long GetTimestamp() => (long)Math.Round(Seconds * TimestampFrequency);
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "AEther-upload-" + Guid.NewGuid().ToString("N"));
        readonly IntPtr window = CreateWindowEx(0, "STATIC", "AEther upload regression", 0x80000000,
            0, 0, 1280, 720, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        public readonly Clock Clock = new();
        public Fixture()
        {
            if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Directory.CreateDirectory(Path.Combine(root, "Common"));
            Directory.CreateDirectory(Path.Combine(root, "Scenes"));
            string source = null;
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "AEther.Vortice", "Shaders", "Common");
                if (Directory.Exists(candidate)) { source = candidate; break; }
            }
            if (source is null) throw new DirectoryNotFoundException("Cannot locate shader sources.");
            foreach (var name in new[] { "Inputs.hlsl", "Present.hlsl" })
                File.Copy(Path.Combine(source, name), Path.Combine(root, "Common", name));
            File.WriteAllText(Path.Combine(root, "Scenes", "Spectrum.hlsl"), """
                float4 PS(PSin input) : SV_Target
                {
                    float2 uv = float2(input.UV.x, HistogramShift - input.UV.y * HistorySpan);
                    float4 a = Histogram0.SampleLevel(Linear, uv, 0);
                    float4 b = Histogram1.SampleLevel(Linear, uv, 0);
                    float4 s = Spectrum0.SampleLevel(Linear, float2(input.UV.x, .5), 0);
                    return float4(.3 * (a.rgb + b.rgb + s.rgb), 1);
                }
                """);
        }
        public Renderer Renderer(int bands, double rate, double seconds) => new(window, 1280, 720, bands, rate,
            seconds, root, timeProvider: Clock);
        public void Dispose()
        {
            DestroyWindow(window);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!Path.GetFullPath(root).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Upload fixture cleanup escaped temporary directory.");
            Directory.Delete(root, true);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr handle);
}

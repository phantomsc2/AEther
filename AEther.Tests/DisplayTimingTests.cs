using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public class DisplayTimingTests
{
    static long Ms(double value) => AudioClock.ConvertTimestamp((long)Math.Round(value * 10000), 10_000_000);

    sealed class Clock : TimeProvider
    {
        public long Milliseconds = 100;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    [Test]
    public void TotalAudioAgeUsesSourceEndAndPredictedPresentationRatherThanArrivalOrWakeTime()
    {
        var clock = new Clock { Milliseconds = 1010 };
        var profile = new PipelineProfile();
        var trace = profile.Track("render.total_audio_age_ms");
        var history = new AudioHistory(1, 100, timeProvider: clock) { Profile = profile };
        history.Post(new(new double[4], 4, DateTime.UnixEpoch)
        {
            CaptureTimestamp = Ms(1008),
            SampleStartTimestamp = Ms(990), SampleEndTimestamp = Ms(1000),
        }, 1);
        history.Advance(0, Ms(1030));
        clock.Milliseconds = 1018;
        history.Advance(0, Ms(1030));
        history.Advance(0); // No prediction: do not substitute a different endpoint under the same label.

        long cursor = 0;
        var samples = new System.Collections.Generic.List<TimingSample>();
        trace.ReadSince(ref cursor, samples);
        Assert.That(samples, Has.Count.EqualTo(2));
        Assert.That(samples.Select(s => s.Milliseconds), Is.All.EqualTo(30).Within(.0001));
        Assert.That(samples.Select(s => s.Timestamp), Is.EqualTo(new[] { Ms(1010), Ms(1018) }));

        var offline = new AudioHistory(1, 100, timeProvider: clock) { Profile = profile };
        offline.Post(new(new double[4], 4, DateTime.UnixEpoch), 1);
        offline.Advance(0, Ms(1030));
        trace.ReadSince(ref cursor, samples);
        Assert.That(samples, Is.Empty, "Untimed replay has no source-to-presentation latency.");
    }

    [Test]
    public void DriverClockMapsPresentationTimeIndependentlyOfHostWakeAndRowArrival()
    {
        var clock = new Clock { Milliseconds = 1000 };
        var history = new AudioHistory(1, 100, timeProvider: clock);
        var anchor = new AudioStreamClock(800, Ms(960), 8000);
        history.Post(new(new double[4], 4, DateTime.UnixEpoch) {
            AnalysisRow = 10, SampleIndex = 800, StreamClock = anchor,
            SampleStartTimestamp = Ms(960), SampleEndTimestamp = Ms(970),
        }, 1);
        history.Advance(0, Ms(1000));
        Check(13);
        clock.Milliseconds = 1020; // Late CPU wake, same intended display time.
        history.Advance(0, Ms(1000));
        Check(13);
        history.Post(new(new double[4], 4, DateTime.UnixEpoch) {
            AnalysisRow = 11, SampleIndex = 880, StreamClock = anchor,
            SampleStartTimestamp = Ms(974), SampleEndTimestamp = Ms(984),
        }, 1);
        history.Advance(0, Ms(1006.25));
        Check(13.625);

        void Check(double row) => Assert.That(history.Shift,
            Is.EqualTo((row + .5) / history.Rows).Within(1e-7));
    }

    [Test]
    public void NewDriverClockEpochClearsOldMappingAndRetainsExactFramePositions()
    {
        var history = new AudioHistory(1, 100, timeProvider: new Clock { Milliseconds = 1000 });
        history.Post(new(new[] { .4, .4, .4, .4 }, 4, DateTime.UnixEpoch) {
            AnalysisRow = 10, StreamClock = new(800, Ms(960), 8000),
            SampleStartTimestamp = Ms(960), SampleEndTimestamp = Ms(970),
        }, 1);
        history.Advance(0, Ms(970));
        history.Post(new(new[] { .8, .8, .8, .8 }, 4, DateTime.UnixEpoch) {
            AnalysisRow = 11, DiscontinuityGeneration = 1, StreamClock = new(880, Ms(990), 8000),
            SampleStartTimestamp = Ms(990), SampleEndTimestamp = Ms(1000),
        }, 1);
        history.Advance(0, Ms(1000));
        Assert.That(history.History[0][40], Is.Zero, "Old epochs cannot be reinterpreted under the new clock mapping.");
        Assert.That(history.History[0][44], Is.EqualTo(.8f));
        Assert.That(history.Shift, Is.EqualTo(11.5 / history.Rows).Within(1e-7));
        Assert.That(history.NewestAge, Is.Zero);
    }

    [Test, Category("GPU")]
    public void CompositorSuppliesNextDisplayTimeInTheQpcDomain()
    {
        using var clock = new CompositionClock();
        var timestamp = clock.NextFrameTimestamp();
        Assert.That(timestamp, Is.Not.Null, "The compositor display estimate must be available on this Windows session.");
        double lead = AudioClock.Seconds(timestamp!.Value - Stopwatch.GetTimestamp());
        TestContext.Out.WriteLine($"Compositor estimate relative to QPC: {lead * 1000:F3} ms");
        Assert.That(Math.Abs(lead), Is.LessThan(1), "Verify the native struct, clock units and COM call.");
    }

    [Test]
    public void PresentationTimelineRejectsEstimateSpurtsButRetainsRealMissedFrames()
    {
        var timeline = new PresentationTimeline();
        double period = System.Diagnostics.Stopwatch.Frequency / 165.0;
        long start = Ms(1000), anchor = Ms(1018);
        Assert.That(timeline.Estimate(start, anchor, period), Is.EqualTo(anchor));
        for (int frame = 1; frame <= 100; frame++)
        {
            // Real wakes stay at 165 Hz; the API alternates a repeated estimate
            // with a two-refresh jump. Neither should enter the animation clock.
            long now = start + (long)Math.Round(frame * period) + Ms(frame % 2 == 0 ? .1 : -.1);
            long raw = anchor + (long)Math.Round((frame - frame % 2) * period);
            Assert.That(timeline.Estimate(now, raw, period),
                Is.EqualTo(anchor + (long)Math.Round(frame * period)));
        }
        Assert.That(timeline.Estimate(start + (long)Math.Round(104 * period), anchor + (long)Math.Round(104 * period), period),
            Is.EqualTo(anchor + (long)Math.Round(104 * period)), "A real four-frame gap must advance by four frames.");
    }

    [Test]
    public void PresentationTimelineReanchorsOnRefreshChangeAndRestore()
    {
        var timeline = new PresentationTimeline();
        double period = System.Diagnostics.Stopwatch.Frequency / 165.0;
        timeline.Estimate(Ms(1000), Ms(1018), period);
        Assert.That(timeline.Estimate(Ms(2000), Ms(2012), period), Is.EqualTo(Ms(2012)));
        Assert.That(timeline.Estimate(Ms(2006), Ms(2020), System.Diagnostics.Stopwatch.Frequency / 120.0), Is.EqualTo(Ms(2020)));
    }

    [Test, Category("GPU")]
    public void HistogramPixelsFollowDisplayTimeAndExpireWithStoppedCaptureAt720p()
    {
        const int width = 1280, height = 720;
        using var window = new Window();
        var clock = new Clock();
        using var renderer = new Renderer(window.Handle, width, height, 1, 100,
            initialScene: "Histogram", timeProvider: clock);
        TestContext.Out.WriteLine($"GPU: {renderer.AdapterDescription}");
        for (int row = 0; row < 8; row++)
        {
            double value = row == 2 ? .8 : 0;
            renderer.PostAudio(new(new[] { 0.0, value, 0, value }, 4, DateTime.UnixEpoch) {
                AnalysisRow = row, SampleIndex = row * 80,
                StreamClock = new(0, 0, 8000),
                SampleStartTimestamp = Ms(row * 10), SampleEndTimestamp = Ms((row + 1) * 10),
            }, 1);
        }
        double first = 0;
        for (int frame = 0; frame < 4; frame++)
        {
            // CPU time advances unevenly, and capture supplies no additional rows.
            clock.Milliseconds = 100 + frame * frame * 7;
            renderer.Render(false, presentationTimestamp: Ms(50 + frame * 5));
            double x = Center(renderer.ReadPixels(false));
            if (frame == 0) first = x;
            // Sharp block edges are quantized to physical pixels.
            Assert.That(x, Is.EqualTo(first - frame * width * .5 * .005 / renderer.Audio.Seconds).Within(.55));
        }
        for (int frame = 0; frame < 60; frame++)
        {
            clock.Milliseconds = 20000 + frame * 10;
            renderer.Render(false, presentationTimestamp: Ms(clock.Milliseconds));
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero,
                $"Every RGB channel must expire after stopped capture, final frame {frame}.");
        }

        double Center(byte[] pixels)
        {
            double sum = 0, weighted = 0;
            for (int x = 0; x < width / 2; x++)
            {
                int value = pixels[((height / 2) * width + x) * 4 + 1];
                sum += value; weighted += value * (x + .5);
            }
            Assert.That(sum, Is.GreaterThan(0), "The historical impulse must be visible.");
            return weighted / sum;
        }
    }

    [Test, Category("GPU")]
    public void HistogramShowsExactBlockIntervalsAndMissingRowsAt720p()
    {
        const int width = 1280, height = 720;
        using var window = new Window();
        using var renderer = new Renderer(window.Handle, width, height, 1, 100, .2,
            initialScene: "Histogram", timeProvider: new Clock());
        foreach (int row in new[] { 0, 2, 4 })
        {
            double[] values = row switch {
                0 => new[] { 0.0, .8, 0, .8 },
                2 => new[] { 0.0, 0, .8, .8 },
                _ => new[] { .8, 0.0, 0, .8 },
            };
            renderer.PostAudio(new(values, 4, DateTime.UnixEpoch) {
                AnalysisRow = row, StreamClock = new(0, 0, 8000),
                SampleStartTimestamp = Ms(row * 10), SampleEndTimestamp = Ms((row + 1) * 10),
            }, 1);
        }
        renderer.Render(false, presentationTimestamp: Ms(60));
        var pixels = renderer.ReadPixels(false);
        for (int x = 0; x < width; x++)
        {
            double age = 2 * Math.Abs((x + .5) / width - .5);
            int block = (int)Math.Floor((60 - age * 200) / 10);
            int channel = block switch { 0 => 1, 2 => 2, 4 => 0, _ => -1 };
            for (int c = 0; c < 3; c++)
            {
                int value = pixels[((height / 2) * width + x) * 4 + c];
                if (c == channel) Assert.That(value, Is.GreaterThan(0), $"block {block}, pixel {x}, channel {c}");
                else Assert.That(value, Is.Zero, $"No interpolation into gaps: block {block}, pixel {x}, channel {c}");
            }
        }
    }

    [Test, Category("GPU")]
    public void HistogramFractionalPixelCoverageScrollsWithoutCatchupSteps()
    {
        const int width = 1280, height = 720;
        using var window = new Window();
        using var renderer = new Renderer(window.Handle, width, height, 1, 100,
            initialScene: "Histogram", timeProvider: new Clock());
        for (int row = 0; row < 12; row++)
        {
            double value = row == 2 ? .8 : 0;
            renderer.PostAudio(new(new[] { 0.0, value, 0, value }, 4, DateTime.UnixEpoch)
            {
                AnalysisRow = row, StreamClock = new(0, 0, 8000),
                SampleStartTimestamp = Ms(row * 10), SampleEndTimestamp = Ms((row + 1) * 10),
            }, 1);
        }
        double first = 0;
        for (int frame = 0; frame < 20; frame++)
        {
            double time = 100 + frame * 1000.0 / 165;
            renderer.Render(false, presentationTimestamp: Ms(time));
            var pixels = renderer.ReadPixels(false);
            double mass = 0, weighted = 0;
            for (int x = 0; x < width / 2; x++)
            {
                double value = pixels[((height / 2) * width + x) * 4 + 1];
                mass += value; weighted += (x + .5) * value;
            }
            Assert.That(mass, Is.EqualTo(.8 * 255 * width * .5 * .01 / renderer.Audio.Seconds).Within(3),
                "A fractional shift must conserve event brightness/area.");
            double center = weighted / mass;
            if (frame == 0) first = center;
            Assert.That(center, Is.EqualTo(first - frame * width * .5 / (165 * renderer.Audio.Seconds)).Within(.09),
                "Event motion must follow fractional display time, without whole-pixel holds and jumps.");
        }
    }

    [Test, Category("GPU")]
    public void DebugShowsStereoSpectrumAndMeanHistoryScrollingLeft()
    {
        const int width = 1280, height = 720;
        using var window = new Window();
        using var renderer = new Renderer(window.Handle, width, height, 1, 100, .2,
            initialScene: "Debug", timeProvider: new Clock());
        for (int row = 0; row < 8; row++)
        {
            bool active = row == 2 || row == 7;
            renderer.PostAudio(new(new[] { 0.0, active ? .8 : 0, 0, 0, 0, active ? .4 : 0, 0, 0 }, 4, DateTime.UnixEpoch) {
                AnalysisRow = row, StreamClock = new(0, 0, 8000),
                SampleStartTimestamp = Ms(row * 10), SampleEndTimestamp = Ms((row + 1) * 10),
            }, 2);
        }
        renderer.Render(false, presentationTimestamp: Ms(80));
        var pixels = renderer.ReadPixels(false);
        int Green(int x) => pixels[((height / 2) * width + x) * 4 + 1];
        Assert.That(Green(1088), Is.GreaterThan(200), "Channel 0 fills half of its strip.");
        Assert.That(Green(1030), Is.Zero, "Channel 0 grows left from the shared seam.");
        Assert.That(Green(1140), Is.GreaterThan(200), "Channel 0 is filled near the shared seam.");
        Assert.That(Green(1190), Is.GreaterThan(200), "Channel 1 fills the start of its strip.");
        Assert.That(Green(1216), Is.Zero, "Channel 1 stops at its own amplitude.");
        double center = Center(pixels, out double mass);
        Assert.That(mass, Is.EqualTo(.6 * 255 * width * .8 * .01 / .2).Within(4),
            "History contains the mean of both channels.");
        renderer.Render(false, presentationTimestamp: Ms(85));
        double next = Center(renderer.ReadPixels(false), out _);
        Assert.That(next, Is.EqualTo(center - width * .8 * .005 / .2).Within(.1));

        static double Center(byte[] frame, out double mass)
        {
            mass = 0;
            double weighted = 0;
            // Isolate the older impulse from the newest one at the history seam.
            for (int x = 0; x < 850; x++)
            {
                double value = frame[((height / 2) * width + x) * 4 + 1];
                mass += value;
                weighted += (x + .5) * value;
            }
            Assert.That(mass, Is.GreaterThan(0));
            return weighted / mass;
        }
    }

    [Test, Category("GPU")]
    public void SpectrumShowsNewBlocksWithoutAveragingOrHoldingAcrossGapsAt720p()
    {
        const int width = 1280, height = 720;
        using var window = new Window();
        var clock = new Clock { Milliseconds = 20 };
        using var renderer = new Renderer(window.Handle, width, height, 1, 100,
            initialScene: "Spectrum", timeProvider: clock);
        for (int row = 0; row < 2; row++)
        {
            double value = row == 0 ? .2 : .8;
            renderer.PostAudio(new(new[] { 0.0, value, 0, value }, 4, DateTime.UnixEpoch) {
                AnalysisRow = row, StreamClock = new(0, 0, 8000), ResidualRms = new[] { .3 },
                SampleStartTimestamp = Ms(row * 10), SampleEndTimestamp = Ms((row + 1) * 10),
            }, 1);
        }
        renderer.Render(false);
        Assert.That(renderer.Audio.Spectrum[0][1], Is.EqualTo(.5f), "Other scenes retain their frame aggregate.");
        Assert.That(renderer.Audio.BlockSpectrum[0][1], Is.EqualTo(.8f));
        Assert.That(renderer.ReadPixels(false)[((height / 2) * width + 1050) * 4 + 1], Is.GreaterThan(0),
            "The actual .8 block extends beyond the .5 average.");
        clock.Milliseconds = 21;
        renderer.Render(false);
        Assert.That(renderer.Audio.BlockResidualSpectrum[0], Is.All.Zero);
        Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero,
            "A display frame without a new block shows a gap, including the residual outline.");
    }

    [TestCase(1280, 720)]
    [TestCase(3200, 2000)]
    [Category("GPU"), Category("Performance")]
    public void MeasureHistogramGpuCostByResolution(int width, int height)
    {
        using var window = new Window();
        var clock = new Clock();
        using var renderer = new Renderer(window.Handle, width, height, 97, 160,
            initialScene: "Histogram", timeProvider: clock);
        var samples = Enumerable.Range(0, 97 * 8).Select(i => (i % 17 + 1) / 32.0).ToArray();
        long next = 0;
        for (; next < 320; next++) Post();
        for (int frame = 0; frame < 16; frame++) Tick(frame);
        renderer.ReadHistogram(0); // Drain GPU warmup without copying the full native-size image.
        var profile = new PipelineProfile();
        renderer.Audio.Profile = profile;
        profile.Describe("Adapter", renderer.AdapterDescription);
        profile.Describe("Resolution", $"{width}x{height}");
        profile.Describe("Path", "Global Arc default; normal hardware fallback when Arc is absent");
        profile.Describe("Scope", "GPU rendering only; excludes live compositor and cross-adapter presentation");
        profile.Start();
        for (int frame = 16; frame < 112; frame++)
        {
            Tick(frame);
            if (frame % 4 == 3) renderer.ReadHistogram(0);
        }
        renderer.ReadHistogram(0); renderer.PollGpuTimings();
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "AEther.Vortice", "Shaders")))
            {
                TestContext.Out.WriteLine(profile.Save(Path.Combine(dir.FullName, "artifacts", "fullscreen-histogram")));
                break;
            }
        TestContext.Out.WriteLine($"{width}x{height}, {renderer.AdapterDescription}");
        foreach (string stage in new[] { "gpu.scene_ms", "gpu.output_ms", "gpu.frame_ms", "render.draw_cpu_ms" })
        {
            var stat = profile.Recent(stage);
            Assert.That(stat, Is.Not.Null, stage);
            TestContext.Out.WriteLine($"{stage}: mean={stat!.Value.Mean:F4} ms, p95={stat.Value.P95:F4} ms");
        }

        void Post() => renderer.PostAudio(new(samples, 97 * Splitter.OutputStride, DateTime.UnixEpoch) { AnalysisRow = next }, 2);
        void Tick(int frame)
        {
            Post(); next++;
            clock.Milliseconds = frame * 6;
            renderer.Render(false);
        }
    }

    sealed class Window : IDisposable
    {
        public IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "AEther display timing regression",
            0x80000000, 0, 0, 1280, 720, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        public Window() { if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(); }
        public void Dispose() => DestroyWindow(Handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr handle);
    }
}

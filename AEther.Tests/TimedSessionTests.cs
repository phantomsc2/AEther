using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public class TimedSessionTests
{
    sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
    sealed class Source : SampleSource, ITimedSampleSource
    {
        public override SampleFormat Format => new(SampleType.Float32.Instance, 8000, 1);
        public event TimedSampleDataHandler TimedDataAvailable;
        public int TimedSubscribers => TimedDataAvailable?.GetInvocationList().Length ?? 0;
        public override void Start()
        {
            var data = new byte[160 * 4];
            TimedDataAvailable?.Invoke(this, data, new(System.Diagnostics.Stopwatch.GetTimestamp()
                - AudioClock.Frames(160, 8000), 0, AudioTimestampQuality.Device));
            DataAvailable?.Invoke(this, data);
        }
        public override void Stop() { }
        public override void Dispose() { }
    }

    static AnalyzerOptions Options() => new() { MinFrequency = 100, MaxFrequency = 1000, TimeResolution = 100,
        MaxInputAgeMilliseconds = 1000 };

    [Test, Category("GPU")]
    public async Task RendererChangePreservesAudioAndReuploadsRetainedHistory()
    {
        using var window = new Window();
        var source = new Source();
        await using var session = new Session(window.Handle, 1280, 720, source, Options());
        var old = session.Renderer;
        var audio = old.Audio;
        int bands = audio.Bands;
        var samples = Enumerable.Repeat(.5, bands * 8).ToArray();
        old.SelectScene("Histogram");
        for (long row = 0; row < 100; row++)
            old.PostAudio(new(samples, bands * Splitter.OutputStride, DateTime.UnixEpoch) { AnalysisRow = row }, 2);
        old.Render(false);
        Assert.That(old.ReadHistogram(0).Any(v => v > 0), Is.True);
        Assert.That(session.NeedsRendererChange("Spectrum", window.Handle), Is.True);

        session.RecreateRenderer(window.Handle, 1280, 720, _ => { }, "Spectrum");
        Assert.That(session.Renderer, Is.Not.SameAs(old));
        Assert.That(session.Renderer.Audio, Is.SameAs(audio));
        Assert.That(session.Renderer.Audio.Profile, Is.SameAs(session.Analyzer.Profile));
        Assert.That(session.Renderer.PreferDisplayAdapter, Is.True);
        Assert.That(session.NeedsRendererChange("Spectrum", window.Handle), Is.False);
        Assert.That(source.TimedSubscribers, Is.EqualTo(1), "Keep the existing capture subscription.");
        session.Renderer.Render(false);
        Assert.That(session.Renderer.LastHistoryUpload.Rows, Is.EqualTo(audio.Rows));
        Assert.That(session.Renderer.ReadHistogram(0), Is.EqualTo(audio.History[0]));

        session.RecreateRenderer(window.Handle, 1280, 720, _ => { }, "Histogram");
        Assert.That(session.Renderer.Audio, Is.SameAs(audio));
        Assert.That(session.Renderer.PreferDisplayAdapter, Is.False);
        session.Renderer.Render(false);
        Assert.That(session.Renderer.LastHistoryUpload.Rows, Is.EqualTo(audio.Rows));
        Assert.That(session.Renderer.ReadHistogram(0), Is.EqualTo(audio.History[0]),
            "New GPU textures must receive the retained history after switching back.");
    }

    [Test, Category("GPU")]
    public async Task SessionConsumesTimedPacketsOnceAndUnsubscribesOnStop()
    {
        using var window = new Window();
        var source = new Source();
        await using var session = new Session(window.Handle, 1280, 720, source, Options());
        Assert.That(source.TimedSubscribers, Is.EqualTo(1));
        Assert.That(source.DataAvailable, Is.Null);
        int rows = 0;
        session.Analyzer.SamplesAnalyzed += (_, e) =>
        {
            Assert.That(e.TimestampQuality, Is.EqualTo(AudioTimestampQuality.Device));
            Assert.That(e.SampleEndTimestamp, Is.Not.Null);
            rows++;
        };
        session.Start();
        await session.StopAsync();
        Assert.That(rows, Is.EqualTo(2));
        Assert.That(source.TimedSubscribers, Is.Zero);
    }

    [Test, Category("GPU")]
    public async Task SubscriberFailureIsReportedWhileSessionIsRunning()
    {
        using var window = new Window();
        var source = new Source();
        var session = new Session(window.Handle, 1280, 720, source, Options());
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.CaptureStopped += (_, error) => { if (error != null) failed.TrySetResult(error); };
        session.Analyzer.SamplesAnalyzed += (_, _) => throw new InvalidOperationException("subscriber failed");
        try
        {
            session.Start();
            Assert.That(await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.TypeOf<InvalidOperationException>());
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.StopAsync());
            Assert.That(source.TimedSubscribers, Is.Zero);
        }
        finally { session.Dispose(); }
    }

    [Test, Category("GPU")]
    public void TimestampedFrameMeanReachesGpuAndExpiresAt720p()
    {
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(temporaryRoot, "AEther-timed-gpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Common"));
        Directory.CreateDirectory(Path.Combine(root, "Scenes"));
        try
        {
            string source = null;
            for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "AEther.Vortice", "Shaders", "Common");
                if (Directory.Exists(candidate)) { source = candidate; break; }
            }
            Assert.That(source, Is.Not.Null);
            foreach (string file in new[] { "Inputs.hlsl", "Present.hlsl" })
                File.Copy(Path.Combine(source, file), Path.Combine(root, "Common", file));
            File.WriteAllText(Path.Combine(root, "Scenes", "TimedProbe.hlsl"), """
                #include "../Common/Inputs.hlsl"
                float4 PS(PSin input) : SV_Target { return float4(Spectrum0.Load(int3(0,0,0)).rgb, 1); }
                """);
            using var window = new Window();
            var clock = new Clock { Milliseconds = 20 };
            using var renderer = new AEther.Vortice.Renderer(window.Handle, 1280, 720, 1, 100,
                shaderDirectory: root, timeProvider: clock);
            renderer.SelectScene("TimedProbe");
            for (int row = 0; row < 2; row++)
            {
                double value = row == 0 ? .2 : .8;
                renderer.PostAudio(new(new[] { value, value, value, value }, 4, DateTime.UnixEpoch)
                {
                    AnalysisRow = row,
                    SampleStartTimestamp = AudioClock.ConvertTimestamp(row * 10, 1000),
                    SampleEndTimestamp = AudioClock.ConvertTimestamp((row + 1) * 10, 1000),
                }, 1);
            }
            renderer.Render(false);
            var pixels = renderer.ReadPixels(false);
            Assert.That(renderer.Audio.Spectrum[0], Is.All.EqualTo(.5f));
            Assert.That(pixels.Where((_, i) => i % 4 != 3).Any(v => v > 100), Is.True);
            clock.Milliseconds = 40; renderer.Render(false);
            Assert.That(renderer.ReadPixels(false), Is.EqualTo(pixels), "No packet must not become a black frame.");
            clock.Milliseconds = 71; renderer.Render(false);
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero);
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Probe cleanup escaped the temporary directory.");
            Directory.Delete(root, true);
        }
    }

    sealed class Window : IDisposable
    {
        public IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "AEther timed source regression",
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

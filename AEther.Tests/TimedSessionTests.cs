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
        public Source(int sampleRate = 8000, int channels = 1) => Format = new(SampleType.Float32.Instance, sampleRate, channels);
        public override SampleFormat Format { get; }
        public bool Emit = true;
        public int Starts, Stops, Disposals;
        public event TimedSampleDataHandler TimedDataAvailable;
        public int TimedSubscribers => TimedDataAvailable?.GetInvocationList().Length ?? 0;
        public override void Start()
        {
            Starts++;
            if (!Emit) return;
            var data = new byte[160 * Format.Size];
            TimedDataAvailable?.Invoke(this, data, new(System.Diagnostics.Stopwatch.GetTimestamp()
                - AudioClock.Frames(160, Format.SampleRate), 0, AudioTimestampQuality.Device));
            DataAvailable?.Invoke(this, data);
        }
        public override void Stop() { Stops++; }
        public override void Dispose() { Disposals++; }
    }

    static AnalyzerOptions Options() => new() { MinFrequency = 100, MaxFrequency = 1000, TimeResolution = 100,
        MaxInputAgeMilliseconds = 1000 };

    [Test, Category("GPU")]
    public async Task AnalysisRestartsKeepCompiledProgramsAcrossEveryGraphicsContractChange()
    {
        using var shaders = new RestartShaders();
        using var window = new Window();
        var source = new Source { Emit = false };
        await using var session = new Session(window.Handle, 1280, 720, source, Options(),
            shaderDirectory: shaders.Root, initialScene: "Probe");
        session.Start();
        var renderer = session.Renderer;
        var frameReady = renderer.FrameReady;
        var originalAnalyzer = session.Analyzer;
        var rejectedSource = new Source { Emit = false };
        var invalid = Options(); invalid.TimeResolution = 0;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await session.RestartAnalysisAsync(rejectedSource, invalid));
        Assert.That(session.Analyzer, Is.SameAs(originalAnalyzer));
        Assert.That(originalAnalyzer.Completion.IsCompleted, Is.False);
        Assert.That(source.Stops, Is.Zero, "Reject invalid settings before stopping the active generation.");
        Assert.That(rejectedSource.TimedSubscribers, Is.Zero);
        Assert.That(rejectedSource.Disposals, Is.Zero, "Failed replacement sources remain caller-owned.");
        rejectedSource.Dispose();
        renderer.SelectScene("Other");
        var other = renderer.ActiveShader.Program.Pixel;
        renderer.SelectScene("Probe");
        var scene = renderer.ActiveShader;
        var program = scene.Program.Pixel;
        var parameter = scene.Parameters.Items.Single(p => p.Name == "Gain");
        scene.Parameters.SetFloat(parameter, .75f);
        // Any attempt to reload source/recompile on settings changes now fails.
        foreach (string file in Directory.GetFiles(Path.Combine(shaders.Root, "Scenes")))
            File.WriteAllText(file, "INVALID SHADER SOURCE");
        File.WriteAllText(Path.Combine(shaders.Root, "Common", "Inputs.hlsl"), "INVALID COMMON SOURCE");

        (string Name, Action<AnalyzerOptions> Edit, int Rate, int Channels)[] edits =
        [
            ("gain", o => o.InputGainDb = 0, 8000, 1),
            ("DFT backend", o => o.UseSwift = false, 8000, 1),
            ("SIMD", o => o.SIMDEnabled = false, 8000, 1),
            ("SWIFT tuning", o => { o.SwiftWidthScale = 2; o.SwiftImbalance = .5; }, 8000, 1),
            ("smoother", o => { o.SmoothingMode = SmoothingMode.Complex; o.SmoothingNoiseFloor = .01; }, 8000, 1),
            ("bypass", o => o.SmoothingEnabled = false, 8000, 1),
            ("splitter", o => { o.TimeWindowSeconds = .3; o.FrequencyWindowOctaves = 3; }, 8000, 1),
            ("queue", o => { o.BufferCapacity = 8; o.MaxInputAgeMilliseconds = 500; }, 8000, 1),
            ("domain meaning", o => { o.MinFrequency = 200; o.MaxFrequency = 2000; }, 8000, 1),
            ("bands", o => o.FrequencyResolution = 24, 8000, 1),
            ("actual tick rate", o => o.TimeResolution = 101, 8000, 1),
            ("history", o => o.HistorySeconds = 3, 8000, 1),
            ("sample rate/channels", _ => { }, 44100, 2),
        ];
        foreach (var edit in edits)
        {
            var oldAnalyzer = session.Analyzer;
            var oldAudio = renderer.Audio;
            var options = Options(); edit.Edit(options);
            var nextSource = new Source(edit.Rate, edit.Channels) { Emit = false };
            await session.RestartAnalysisAsync(nextSource, options);
            Assert.That(oldAnalyzer.Completion.IsCompletedSuccessfully, Is.True, edit.Name);
            Assert.That(source.TimedSubscribers, Is.Zero, edit.Name);
            Assert.That(source.Stops, Is.EqualTo(1), edit.Name);
            Assert.That(source.Disposals, Is.EqualTo(1), edit.Name);
            Assert.That(nextSource.TimedSubscribers, Is.EqualTo(1), edit.Name);
            source = nextSource;
            session.Start();
            Assert.That(source.Starts, Is.EqualTo(1), edit.Name);
            Assert.That(session.Renderer, Is.SameAs(renderer), edit.Name);
            Assert.That(renderer.FrameReady, Is.SameAs(frameReady), edit.Name);
            Assert.That(renderer.Input, Is.EqualTo(options.GetGraphicsInput(source.Format)), edit.Name);
            Assert.That(renderer.ActiveShader, Is.SameAs(scene), edit.Name);
            Assert.That(scene.Program.Pixel, Is.SameAs(program), edit.Name);
            Assert.That(renderer.Scenes.Single(s => s.Name == "Other").Program.Pixel, Is.SameAs(other), edit.Name);
            Assert.That(renderer.Scenes.Single(s => s.Name == "Lazy").Parameters, Is.Null, edit.Name);
            Assert.That(scene.Parameters.GetFloat(parameter), Is.EqualTo(.75f), edit.Name);
            Assert.That(renderer.Audio, Is.Not.SameAs(oldAudio), edit.Name);
            Assert.That(renderer.Audio.Profile, Is.SameAs(session.Analyzer.Profile), edit.Name);
            Assert.That(renderer.ReadHistogram(0), Is.All.Zero, edit.Name);
            Assert.That(renderer.ReadHistogram(0).Length, Is.EqualTo(renderer.Audio.Bands * renderer.Audio.Rows * 4));

            // Row zero must work again; the preceding analysis generation had row zero too.
            int bands = renderer.Audio.Bands;
            renderer.PostAudio(new(Enumerable.Repeat(.5, bands * 4).ToArray(), bands * 4, DateTime.UnixEpoch)
                { AnalysisRow = 0 }, 1);
            renderer.Render(false);
            Assert.That(renderer.ReadHistogram(0).Any(v => v > 0), Is.True, edit.Name);
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Any(v => v > 0), Is.True, edit.Name);
        }
    }

    sealed class RestartShaders : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AEther-restart-" + Guid.NewGuid().ToString("N"));
        public RestartShaders()
        {
            Directory.CreateDirectory(Path.Combine(Root, "Common"));
            Directory.CreateDirectory(Path.Combine(Root, "Scenes"));
            string common = null;
            for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "AEther.Vortice", "Shaders", "Common");
                if (Directory.Exists(candidate)) { common = candidate; break; }
            }
            foreach (string file in new[] { "Inputs.hlsl", "Present.hlsl" })
                File.Copy(Path.Combine(common, file), Path.Combine(Root, "Common", file));
            foreach (string name in new[] { "Probe", "Other", "Lazy" })
                File.WriteAllText(Path.Combine(Root, "Scenes", name + ".hlsl"), """
                    #include "../Common/Inputs.hlsl"
                    cbuffer Parameters { float Gain = .5; };
                    float4 PS(PSin input) : SV_Target { return float4(Gain * Spectrum0.Load(int3(0,0,0)).rgb, 1); }
                    """);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cleanup escaped temporary directory.");
            Directory.Delete(Root, true);
        }
    }

    [Test, Category("GPU")]
    public async Task SceneChangesPreserveRendererAudioHistoryAndCaptureSubscription()
    {
        using var window = new Window();
        var source = new Source();
        await using var session = new Session(window.Handle, 1280, 720, source, Options());
        var renderer = session.Renderer;
        var audio = renderer.Audio;
        var adapter = renderer.AdapterDescription;
        var samples = Enumerable.Repeat(.5, audio.Bands * 8).ToArray();
        renderer.SelectScene("Histogram");
        for (long row = 0; row < 100; row++)
            renderer.PostAudio(new(samples, audio.Bands * Splitter.OutputStride, DateTime.UnixEpoch) { AnalysisRow = row }, 2);
        renderer.Render(false);
        Assert.That(renderer.ReadHistogram(0).Any(v => v > 0), Is.True);
        foreach (var scene in new[] { "Spectrum", "Histogram", "MandelFluidv4", "Mandelbox" })
        {
            renderer.SelectScene(scene);
            renderer.Render(false);
            Assert.That(session.Renderer, Is.SameAs(renderer), scene);
            Assert.That(renderer.Audio, Is.SameAs(audio), scene);
            Assert.That(renderer.AdapterDescription, Is.EqualTo(adapter), scene);
            Assert.That(audio.Profile, Is.SameAs(session.Analyzer.Profile));
            Assert.That(renderer.ReadHistogram(0), Is.EqualTo(audio.History[0]));
            Assert.That(renderer.LastHistoryUpload.Rows, Is.Zero, "Scene changes must not rebuild GPU history.");
            Assert.That(source.TimedSubscribers, Is.EqualTo(1), "Keep the existing capture subscription.");
        }
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

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public partial class ShaderRenderingTests
{
    [TestCase(true, false), TestCase(false, false), TestCase(true, true), TestCase(false, true), Category("GPU")]
    public void EmbeddedDebugSharesAudioWithoutChangingTheCanvas(bool drawCanvas, bool lowCost)
    {
        using var shaders = new SceneShaders("Spectrum");
        File.Copy(Path.Combine(ShaderDirectory, "Scenes", "Debug.hlsl"), Path.Combine(shaders.Path, "Scenes", "Debug.hlsl"));
        using var window = new HiddenWindow();
        using var debugWindow = new HiddenWindow();
        using var referenceWindow = new HiddenWindow();
        using var canvasReferenceWindow = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 160, 90, 8, 60, 1,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: "Spectrum");
        using var reference = new Renderer(referenceWindow.Handle, 300, 80, 8, 60, 1,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: "Debug");
        using var canvasReference = new Renderer(canvasReferenceWindow.Handle, 160, 90, 8, 60, 1,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: "Spectrum");
        var canvasParameters = renderer.ActiveShader!.Parameters;
        renderer.AttachDebugOutput(debugWindow.Handle, 300, 80, lowCost: lowCost);
        renderer.Render(false);
        var initialCanvas = renderer.ReadPixels(false);
        var canvasPasses = 0;
        renderer.TransportPassMarker = (name, start) => { if (start && name == "Scene") canvasPasses++; };
        for (int frame = 0; frame < 90; frame++)
        {
            if (frame < 60)
            {
                var samples = new double[8 * 8];
                for (int i = 0; i < samples.Length; i += 4)
                {
                    samples[i] = .2 + .5 * (i % 16) / 16.0;
                    samples[i + 1] = frame % 3 == 0 ? .7 : 0;
                    samples[i + 2] = .3;
                    samples[i + 3] = .8;
                }
                foreach (var target in new[] { renderer, reference, canvasReference })
                    target.PostAudio(new SampleEvent<double>(samples, 32, DateTime.UnixEpoch) { AnalysisRow = frame }, 2);
            }
            clock.Advance();
            var previousPasses = canvasPasses;
            if (frame == 30) renderer.SetGraphicsOptions(renderer.Options with { Exposure = 12 });
            renderer.Render(false, renderCanvas: drawCanvas, renderDebug: true);
            Assert.That(canvasPasses - previousPasses, Is.EqualTo(drawCanvas ? 1 : 0),
                "The retained canvas scene must not run while only Debug is rendered.");
            reference.Render(false);
            canvasReference.Render(false);
            Assert.That(renderer.DebugOutputError, Is.Null);
            Assert.That(renderer.ReadDebugPixels(), Is.EqualTo(reference.ReadPixels(false)).Within(lowCost ? 1 : 0), $"shared audio frame {frame}");
            Assert.That(renderer.SceneName, Is.EqualTo("Spectrum"));
            Assert.That(renderer.ActiveShader!.Parameters, Is.SameAs(canvasParameters));
            if (!drawCanvas) Assert.That(renderer.ReadPixels(false), Is.EqualTo(initialCanvas));
            else Assert.That(renderer.ReadPixels(false), Is.EqualTo(canvasReference.ReadPixels(false)), "Debug must not alter canvas rendering.");
            if (frame == 20)
            {
                // Canvas Debug edits and reloads must not affect preview defaults.
                foreach (var target in new[] { renderer })
                {
                    target.CompileScene("Debug");
                    var parameters = target.Scenes.Single(s => s.Name == "Debug").Parameters!;
                    parameters.SetBoolean(parameters.Items.Single(p => p.Name == "CombinedLevel"), true);
                }
                Assert.That(renderer.ReloadScene("Debug"), Is.True);
            }
        }
        var draws = renderer.DebugDrawCount;
        clock.Advance(); renderer.Render(false);
        Assert.That(renderer.DebugDrawCount, Is.EqualTo(draws), "Hidden Debug must do no drawing.");
        renderer.ResizeDebugOutput(200, 100);
        clock.Advance(); renderer.Render(false, renderCanvas: false, renderDebug: true);
        Assert.That(renderer.ReadDebugPixels().Length, Is.EqualTo(200 * 100 * 4));
        renderer.RestartAudio(new(new Domain(30, 12, 8), 60));
        clock.Advance(); renderer.Render(false, renderCanvas: false, renderDebug: true);
        Assert.That(renderer.ReadDebugPixels().Where((_, i) => i % 4 != 3), Is.All.Zero);
        var path = Path.Combine(shaders.Path, "Scenes", "Debug.hlsl");
        var source = File.ReadAllText(path);
        File.WriteAllText(path, "THIS IS INVALID HLSL");
        Assert.That(renderer.ReloadScene("Debug"), Is.True, "Inactive scenes compile lazily.");
        clock.Advance();
        Assert.DoesNotThrow(() => renderer.Render(false, renderDebug: true));
        Assert.That(renderer.DebugOutputError, Is.Not.Null, "A diagnostic shader failure must be isolated from the canvas.");
        Assert.That(renderer.SceneName, Is.EqualTo("Spectrum"));
        File.WriteAllText(path, source);
        Assert.That(renderer.ReloadScene("Debug"), Is.True);
        renderer.ClearDebugOutputError();
        clock.Advance(); renderer.Render(false, renderDebug: true);
        Assert.That(renderer.DebugOutputError, Is.Null);
        renderer.DetachDebugOutput();
        Assert.That(renderer.HasDebugOutput, Is.False);
    }

    [TestCase(false), TestCase(true), Category("GPU")]
    public void DebugCombinesLiveStereoBarsWithMeanHistory(bool combined)
    {
        const int width = 1000, height = 64, bands = 8;
        using var shaders = new SceneShaders("Debug");
        File.Copy(Path.Combine(ShaderDirectory, "Scenes", "Histogram.hlsl"),
            Path.Combine(shaders.Path, "Scenes", "Histogram.hlsl"));
        using var window = new HiddenWindow();
        using var historyWindow = new HiddenWindow();
        var clock = new RenderClock();
        using var debug = new Renderer(window.Handle, width, height, bands, 24, 1,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: "Debug");
        using var history = new Renderer(historyWindow.Handle, 1600, height, bands, 24, 1,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: "Histogram");
        foreach (var renderer in new[] { debug, history })
        {
            var parameters = renderer.ActiveShader!.Parameters!;
            parameters.SetBoolean(parameters.Items.Single(p => p.Name == "CombinedLevel"), combined);
        }
        long row = 0;
        for (int frame = 0; frame < 150; frame++)
        {
            // 24 Hz blocks on a 60 Hz display: fractional scrolling, missing rows,
            // ring wrap, isolated frequency rows and distinct stereo channels.
            bool posted = frame % 5 == 0 || frame % 5 == 2;
            if (posted)
            {
                var samples = new double[bands * 8];
                for (int c = 0; c < 2; c++)
                {
                    int offset = (c * bands + 2) * 4;
                    samples[offset] = c == 0 ? .25 : .75;
                    samples[offset + 1] = .5;
                    samples[offset + 2] = c == 0 ? .75 : .25;
                    samples[offset + 3] = frame == 145 ? 1.5 : .875;
                }
                if (frame == 50) row += 2;
                foreach (var renderer in new[] { debug, history })
                    renderer.PostAudio(new SampleEvent<double>(samples, bands * 4, DateTime.UnixEpoch)
                        { AnalysisRow = row }, 2);
                row++;
            }
            clock.Advance(); debug.Render(false); history.Render(false);
            if (frame is not (0 or 1 or 50 or 51 or 130 or 131 or 145 or 146 or 149)) continue;
            var pixels = debug.ReadPixels(false);
            var reference = history.ReadPixels(false);
            for (int y = 0; y < height; y++)
            for (int x = 0; x < 800; x++)
            for (int c = 0; c < 3; c++)
            {
                double mean = .5 * (reference[4 * (y * 1600 + x) + c] + reference[4 * (y * 1600 + 1599 - x) + c]);
                Assert.That(pixels[4 * (y * width + x) + c],
                    Is.EqualTo(mean).Within(1),
                    $"history frame {frame}, {x},{y}, channel {c}");
            }
            // Interior probes are well away from antialiasing and the total cap.
            foreach (int x in new[] { 810, 840, 870, 910, 940, 970 })
            for (int c = 0; c < 3; c++)
            {
                double amplitude = c == 1 ? .5 : (x < 900) == (c == 0) ? .25 : .75;
                double position = x < 900 ? (900 - (x + .5)) / 100 : (x + .5 - 900) / 100;
                bool filled = posted && position < amplitude;
                bool totalFilled = posted && position < (frame == 145 ? 1.5 : .875);
                Assert.That(pixels[4 * (44 * width + x) + c], Is.EqualTo(!combined && filled ? 255 : totalFilled ? 64 : 0),
                    $"live frame {frame}, x={x}, channel {c}");
                Assert.That(pixels[4 * (4 * width + x) + c], Is.Zero, "Inactive frequency bin");
            }
            if (posted)
            {
                int background = frame == 145 ? 999 : 985;
                Assert.That(pixels.Skip(4 * (44 * width + background)).Take(3), Is.All.EqualTo(64).Within(1),
                    "The total fills a quarter-intensity grey bar, including clipped totals.");
                if (frame != 145)
                    Assert.That(pixels.Skip(4 * (44 * width + 988)).Take(3), Is.All.LessThanOrEqualTo(64),
                        "The antialiased total edge must not become a white cap.");
            }
        }
        debug.RestartAudio(new(new Domain(30, 12, bands), 24));
        clock.Advance(); debug.Render(false);
        Assert.That(debug.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero);
    }

    [TestCase("Debug"), TestCase("Spectrum"), TestCase("Histogram"), Category("GPU"), Category("Performance")]
    public void AnalysisSceneGpuTimingAt720p(string scene)
    {
        using var shaders = new SceneShaders(scene);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        var profile = new PipelineProfile();
        using var renderer = new Renderer(window.Handle, 1280, 720, 128, 60, 2,
            shaderDirectory: shaders.Path, timeProvider: clock, profile: profile, initialScene: shaders.Scene);
        var samples = new double[128 * 8];
        for (int frame = 0; frame < 360; frame++)
        {
            for (int channel = 0; channel < 2; channel++)
            for (int bin = 0; bin < 128; bin++)
            {
                int i = (channel * 128 + bin) * 4;
                double tone = Math.Exp(-Math.Pow((bin - 24 - channel * 4) / 2.0, 2));
                double harmonic = Math.Exp(-Math.Pow((bin - 70) / 3.0, 2));
                double beat = Math.Exp(-(frame % 30) / 2.0);
                samples[i] = .5 * tone;
                samples[i + 1] = .7 * beat * (1 - bin / 128.0);
                samples[i + 2] = .35 * harmonic;
                samples[i + 3] = Math.Max(samples[i], Math.Max(samples[i + 1], samples[i + 2]));
            }
            renderer.PostAudio(new SampleEvent<double>(samples, 128 * 4, DateTime.UnixEpoch)
                { AnalysisRow = frame }, 2);
            clock.Advance(); renderer.Render(false);
            // Bound the queue; timing comes from GPU timestamp queries, not readback duration.
            renderer.ReadPixels(false);
        }
        var timing = profile.Recent("gpu.scene_ms");
        Assert.That(timing, Is.Not.Null);
        TestContext.Out.WriteLine($"{scene}, 1280x720, {renderer.AdapterDescription}: GPU scene mean {timing!.Value.Mean:F4} ms, p95 {timing.Value.P95:F4} ms");
        var directory = Path.GetFullPath(Path.Combine(ShaderDirectory, "..", "..", "artifacts", "debug-scene"));
        Directory.CreateDirectory(directory);
        var capture = Path.Combine(directory, scene + "-1280x720.rgba");
        File.WriteAllBytes(capture, renderer.ReadPixels(false));
        TestContext.AddTestAttachment(capture, "1280x720 RGBA8 analysis scene");
    }

    const int Width = 96, Height = 54, Bands = 32;
    [Test, Category("GPU")]
    public void SpectrumComparisonShowsLogLeftAndComplexRightFromTheSameStereoAverage()
    {
        const int width = 128, height = 72;
        using var shaders = new SceneShaders("Spectrum");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, width, height, Bands, 100,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        renderer.SelectScene("Spectrum");
        var parameters = renderer.ActiveShader!.Parameters!;
        parameters.SetBoolean(parameters.Items.Single(p => p.Name == "CompareSmoothers"), true);
        var output = new double[Bands * 4];
        for (int k = 0; k < Bands; k++) output[k * 4] = .25;
        renderer.PostAudio(new SampleEvent<double>(output, output.Length, DateTime.UnixEpoch)
            { AnalysisRow = 0, ObservedAmplitude = Enumerable.Repeat(.5, Bands).ToArray(),
                LogSmoothedAmplitude = Enumerable.Repeat(.25, Bands).ToArray(),
                ComplexSmoothedAmplitude = Enumerable.Repeat(.75, Bands).ToArray() }, 1);
        clock.Advance(); renderer.Render(false);
        var pixels = renderer.ReadPixels(false);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int offset = 4 * (y * width + x);
            double position = 2 * Math.Abs((x + .5) / width - .5);
            bool marker = Math.Abs(position - .5) < 1.5 / width;
            bool a = x < width / 2 && position < .25;
            bool b = x >= width / 2 && position < .75;
            Assert.That(pixels[offset], Is.EqualTo(marker || a ? 255 : 0));
            Assert.That(pixels[offset + 1], Is.EqualTo(marker || b ? 255 : 0));
            Assert.That(pixels[offset + 2], Is.EqualTo(marker || b ? 255 : 0));
        }
    }

    [Test, Category("GPU")]
    public void SpectrumLayerVisibilityAndRawFallback([Range(0, 15)] int layers, [Values(false, true)] bool showSum)
    {
        using var shaders = new SceneShaders("Spectrum");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        renderer.SelectScene("Spectrum");
        var parameters = renderer.ActiveShader!.Parameters!;
        string[] names = ["ShowS", "ShowT", "ShowB", "ShowR"];
        for (int layer = 0; layer < names.Length; layer++)
            parameters.SetBoolean(parameters.Items.Single(p => p.Name == names[layer]), (layers & (1 << layer)) != 0);
        parameters.SetBoolean(parameters.Items.Single(p => p.Name == "ShowSum"), showSum);
        var samples = new double[Bands * 8];
        for (int i = 0; i < samples.Length; i += 4)
        {
            samples[i] = .25; samples[i + 1] = .5; samples[i + 2] = .75; samples[i + 3] = .875;
        }
        // Different stereo magnitudes and a negative residual exercise packing and absolute value.
        var residual = Enumerable.Repeat(-.625, Bands).Concat(Enumerable.Repeat(.375, Bands)).ToArray();
        renderer.PostAudio(new(samples, Bands * 4, DateTime.UnixEpoch) { MomentaryResidual = residual }, 2);
        clock.Advance(); renderer.Render(false);
        var pixels = renderer.ReadPixels(false);
        double[] amplitude = [.25, .5, .75];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                for (int c = 0; c < 3; c++)
                {
                    double position = 2 * Math.Abs((x + .5) / Width - .5);
                    double value = layers == 0 ? .875 : (layers & (1 << c)) != 0 ? amplitude[c] : 0;
                    if (showSum && layers != 0)
                    {
                        value = 0;
                        for (int layer = 0; layer < 3; layer++)
                            if ((layers & (1 << layer)) != 0) value += amplitude[layer];
                        if ((layers & 8) != 0) value += x < Width / 2 ? .625 : .375;
                    }
                    value = position < value ? 1 : 0;
                    if (!showSum && (layers & 8) != 0 && position < (x < Width / 2 ? .625 : .375))
                        value = Math.Max(value, .25);
                    Assert.That(pixels[(y * Width + x) * 4 + c],
                        Is.EqualTo((int)Math.Round(value * 255)).Within(1), $"layers {layers}, pixel {x},{y}, channel {c}");
                }
    }

    static string ShaderDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "AEther.Vortice", "Shaders");
                if (Directory.Exists(Path.Combine(path, "Scenes"))) return path;
            }
            throw new DirectoryNotFoundException("Cannot locate the shader source tree.");
        }
    }

    // Compile only the scene under test.
    sealed class SceneShaders : IDisposable
    {
        readonly string temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        public string Path { get; }
        public string Scene { get; }
        public SceneShaders(string scene)
        {
            Scene = scene;
            Path = System.IO.Path.Combine(temporaryRoot, "AEther-silence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Scenes"));
            var common = System.IO.Path.Combine(ShaderDirectory, "Common");
            foreach (var source in Directory.EnumerateFiles(common, "*", SearchOption.AllDirectories))
            {
                var target = System.IO.Path.Combine(Path, "Common", System.IO.Path.GetRelativePath(common, source));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
            File.Copy(System.IO.Path.Combine(ShaderDirectory, "Scenes", scene + ".hlsl"),
                System.IO.Path.Combine(Path, "Scenes", scene + ".hlsl"));
        }
        public void Dispose()
        {
            if (!System.IO.Path.GetFullPath(Path).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Silence fixture cleanup escaped the temporary directory.");
            Directory.Delete(Path, true);
        }
    }







    [Test, Category("GPU")]
    public void GraphicsHistoryChangesPreserveFluidAndShaderState()
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new FluidClock(120);
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        clock.Frame = 1; renderer.Render(false);
        var fluid = renderer.Fluid;
        fluid.SeedFilamentsForCheck(speed: 2);
        var dye = fluid.ReadDye();
        var material = fluid.ReadAge();
        var velocity = fluid.ReadVelocity();
        var program = renderer.ActiveShader!.Program.Pixel;
        renderer.SetGraphicsOptions(new(.5));
        Assert.That(renderer.Fluid, Is.SameAs(fluid));
        Assert.That(renderer.ActiveShader.Program.Pixel, Is.SameAs(program));
        Assert.That(fluid.ReadDye(), Is.EqualTo(dye));
        Assert.That(fluid.ReadAge(), Is.EqualTo(material));
        Assert.That(fluid.ReadVelocity(), Is.EqualTo(velocity));
        clock.Frame = 2; renderer.Render(false);
        Assert.That(fluid.LastMaterialSteps, Is.EqualTo(1), "Transport continues on the next display frame.");
    }

    [TestCase(false), TestCase(true), Category("GPU")]
    public void MandelFluidV4ProjectionPreservesRotation(bool periodic)
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        var parameters = renderer.ActiveShader!.Parameters!;
        parameters.SetBoolean(parameters.Items.Single(p => p.Name == "Fluid").Children
            .Single(p => p.Name == "PeriodicBoundary"), periodic);
        clock.Advance(); renderer.Render(false);
        Assert.That(renderer.Fluid.Width, Is.EqualTo(512));
        var check = renderer.Fluid.CheckProjection(renderer.FluidPrograms);
        TestContext.Progress.WriteLine($"V4 periodic={periodic}: divergence {check.Before:G6} -> {check.After:G6}, curl error {check.CurlError:G6}");
        Assert.That(check.After, Is.LessThan(.01 * check.Before));
        Assert.That(check.CurlError, Is.LessThan(1e-4));
    }

    [TestCase(30), TestCase(60), TestCase(120), TestCase(165), Category("GPU")]
    public void MandelFluidV4RegularClockDoesNotBatchSimulationSteps(int rate)
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new FluidClock(rate);
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        renderer.Audio.Profile = new PipelineProfile();
        var samples = Enumerable.Repeat(.1, Bands * 4).ToArray();
        for (int frame = 1; frame <= 60; frame++)
        {
            renderer.PostAudio(new(samples, Bands * 4, DateTime.UnixEpoch) { AnalysisRow = frame }, 1);
            clock.Frame = frame; renderer.Render(false);
            int expected = frame * 60 / rate - (frame - 1) * 60 / rate + (frame == 1 ? 2 : 0);
            Assert.That(renderer.Fluid.LastSteps, Is.EqualTo(expected), $"Frame {frame}, {rate} Hz");
            Assert.That(renderer.Fluid.LastMaterialSteps, Is.EqualTo(1), "Pigment must advance on every display frame.");
        }
        renderer.ReadPixels(false);
        renderer.PollGpuTimings();
        Assert.That(renderer.Audio.Profile.Recent("gpu.fluid_pressure_ms"), Is.Not.Null);
        Assert.That(renderer.InvalidGpuTimingFrames, Is.Zero);
    }

    [TestCase(60), TestCase(1000), Category("GPU")]
    public void MandelFluidV4EmitsAndFlowsWithNormalPresentationLatency(int analysisRate)
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new FluidClock(1000) { Frame = 1000 };
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, analysisRate,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        var samples = Enumerable.Repeat(.1, Bands * 4).ToArray();
        long row = analysisRate - 1;
        // Timestamped live rows end 8 ms behind presentation, like loopback audio
        // rendered for the next compositor tick. Keep that delay at both analysis rates.
        for (int frame = 1; frame <= 12; frame++)
        {
            clock.Frame = 1000 + frame * 17;
            double newestEnd = (clock.Frame - 8) / 1000.0;
            while ((row + 1.0) / analysisRate <= newestEnd)
            {
                renderer.PostAudio(new(samples, Bands * 4, DateTime.UnixEpoch)
                {
                    AnalysisRow = row,
                    SampleStartTimestamp = AudioClock.Frames(row, analysisRate),
                    SampleEndTimestamp = AudioClock.Frames(row + 1, analysisRate),
                }, 1);
                row++;
            }
            renderer.Render(false);
        }
        Assert.That(renderer.Fluid.ReadVelocity().Any(v => Math.Abs(v) > 1e-5), Is.True,
            "Ordinary capture/display delay must not disable the inlet velocity.");
        Assert.That(renderer.Fluid.ReadDye().Where((_, i) => i % 4 == 3).Max(), Is.GreaterThan(.01),
            "Live S must emit material even when analysis rows are shorter than presentation latency.");
        Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Max(), Is.GreaterThan(10));

        // Once the last row is genuinely stale, fresh material and inlet forcing stop.
        clock.Frame += 100;
        renderer.Fluid.Clear();
        renderer.Render(false);
        Assert.That(renderer.Fluid.ReadVelocity(), Is.All.Zero);
        Assert.That(renderer.Fluid.ReadDye(), Is.All.Zero);
    }

    sealed class FluidClock(int rate) : TimeProvider
    {
        public long Frame;
        public override long TimestampFrequency => rate;
        public override long GetTimestamp() => Frame;
    }

    [TestCase(120), TestCase(165), Category("GPU")]
    public void MandelFluidV4FastMaterialMovesBetweenPressureUpdates(int rate)
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new FluidClock(rate);
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 1000,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        clock.Frame = 1; renderer.Render(false);
        renderer.Fluid.SeedFilamentsForCheck(speed: 10);
        var previous = renderer.Fluid.ReadDye();
        bool movedWithoutPressure = false;
        for (int frame = 2; frame <= 14; frame++)
        {
            clock.Frame = frame; renderer.Render(false);
            Assert.That(renderer.Fluid.LastMaterialSteps, Is.EqualTo(1));
            var current = renderer.Fluid.ReadDye();
            Assert.That(current.All(v => float.IsFinite(v) && v >= 0), Is.True);
            if (renderer.Fluid.LastSteps == 0)
            {
                Assert.That(current.Zip(previous, (a, b) => Math.Abs(a - b)).Max(), Is.GreaterThan(.001),
                    "High-speed transport must not freeze on frames without a pressure solve.");
                movedWithoutPressure = true;
            }
            previous = current;
        }
        Assert.That(movedWithoutPressure, Is.True);
    }

    [Test, Category("GPU")]
    public void MandelFluidV4ParameterCopiesFollowEditsAndReset()
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        var parameters = renderer.ActiveShader!.Parameters!;
        var fluid = parameters.Items.Single(p => p.Name == "Fluid");
        var samples = Enumerable.Repeat(.1, Bands * 4).ToArray();
        long row = 0;
        Tick();
        Assert.That(renderer.Fluid.ReadVelocity().Any(v => Math.Abs(v) > 1e-5), Is.True);
        parameters.SetFloat(fluid.Children.Single(p => p.Name == "EmissionVelocity"), 0);
        renderer.Fluid.Clear(); Tick();
        Assert.That(renderer.Fluid.ReadVelocity(), Is.All.Zero, "Live changes must reach cached pass parameters.");
        parameters.Reset();
        renderer.Fluid.Clear(); Tick();
        Assert.That(renderer.Fluid.ReadVelocity().Any(v => Math.Abs(v) > 1e-5), Is.True, "Reset must invalidate cached pass parameters.");
        void Tick()
        {
            renderer.PostAudio(new(samples, Bands * 4, DateTime.UnixEpoch) { AnalysisRow = row++ }, 1);
            clock.Advance(); renderer.Render(false);
        }
    }

    [TestCase(false), TestCase(true), Category("GPU")]
    public void MandelFluidV4ExtremeForcingRemainsFiniteAndBounded(bool periodic)
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        var parameters = renderer.ActiveShader!.Parameters!;
        foreach (var path in new[] { "Fluid.EmissionVelocity", "Fluid.VelocityGain", "Fluid.Curl",
            "Fluid.Viscosity", "Emitter.Rate", "Emitter.MaterialGain", "Emitter.Capacity" })
        {
            var parts = path.Split('.');
            parameters.SetFloat(parameters.Items.Single(p => p.Name == parts[0]).Children.Single(p => p.Name == parts[1]), 1);
        }
        parameters.SetBoolean(parameters.Items.Single(p => p.Name == "Fluid").Children.Single(p => p.Name == "PeriodicBoundary"), periodic);
        var samples = Enumerable.Repeat(1e6, Bands * 4).ToArray();
        for (int frame = 0; frame < 120; frame++)
        {
            renderer.PostAudio(new(samples, Bands * 4, DateTime.UnixEpoch) { AnalysisRow = frame }, 1);
            clock.Advance(); renderer.Render(false);
            if (frame % 30 != 29) continue;
            var velocity = renderer.Fluid.ReadVelocity();
            var dye = renderer.Fluid.ReadDye();
            var age = renderer.Fluid.ReadAge();
            Assert.That(velocity.All(v => float.IsFinite(v) && Math.Abs(v) < 1e4), Is.True);
            Assert.That(dye.All(v => float.IsFinite(v) && v >= 0), Is.True);
            Assert.That(age.All(float.IsFinite), Is.True);
            double peak = dye.Where((_, i) => i % 4 == 3).Max();
            Assert.That(peak, Is.InRange(.01, 100.001), "Extreme forcing must retain material without exceeding capacity.");
        }
    }

    [Test, Category("GPU")]
    public void MandelFluidV4KeepsSingleCellFilamentsWithoutArtificialDiffusion()
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        clock.Advance(); renderer.Render(false);
        renderer.Fluid.SeedFilamentsForCheck();
        for (int frame = 0; frame < 60; frame++) { clock.Advance(); renderer.Render(false); }
        var dye = renderer.Fluid.ReadDye();
        int width = renderer.Fluid.Width, height = renderer.Fluid.Height;
        for (int y = height / 3; y < 2 * height / 3; y++)
        for (int x = width / 4 + 4; x < 3 * width / 4 - 4; x += 4)
        {
            int cell = y * width + x;
            Assert.That(dye[4 * cell + 3], Is.GreaterThan(.99f));
            Assert.That(dye[4 * (cell + 1) + 3], Is.LessThan(.01f));
            Assert.That(dye[4 * cell], Is.EqualTo(.2 * Math.Exp(-1 / renderer.Audio.Seconds)).Within(.002));
        }
    }










    sealed class RenderClock : TimeProvider
    {
        long frame;
        public override long TimestampFrequency => 60;
        public override long GetTimestamp() => frame;
        public void Advance() => frame++;
    }

    sealed class HiddenWindow : IDisposable
    {
        public IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "AEther shader rendering tests",
            0x80000000, 0, 0, Width, Height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        public HiddenWindow()
        {
            if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        }
        public void Dispose() => DestroyWindow(Handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr handle);
    }
}

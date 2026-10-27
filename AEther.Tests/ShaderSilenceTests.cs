using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public class ShaderSilenceTests
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
            renderer.Render(false, renderCanvas: drawCanvas, renderDebug: true);
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
                // Reload the inactive Debug scene and preserve the shared checkbox.
                foreach (var target in new[] { renderer, reference })
                {
                    var parameters = target.Scenes.Single(s => s.Name == "Debug").Parameters!;
                    parameters.SetBoolean(parameters.Items.Single(p => p.Name == "CombinedLevel"), true);
                }
                Assert.That(renderer.ReloadScene("Debug"), Is.True);
            }
        }
        var draws = renderer.DebugDrawCount;
        clock.Advance(); renderer.Render(false);
        Assert.That(renderer.DebugDrawCount, Is.EqualTo(draws), "Hidden Debug must do no drawing.");
        for (int frame = 0; frame < MinimumDecayFrames; frame++)
        {
            clock.Advance(); renderer.Render(false, renderCanvas: false, renderDebug: true);
        }
        for (int frame = 0; frame < FinalFrames; frame++)
        {
            clock.Advance(); renderer.Render(false, renderCanvas: false, renderDebug: true);
            Assert.That(renderer.ReadDebugPixels().Where((_, i) => i % 4 != 3), Is.All.Zero);
        }
        renderer.ResizeDebugOutput(200, 100);
        clock.Advance(); renderer.Render(false, renderCanvas: false, renderDebug: true);
        Assert.That(renderer.ReadDebugPixels().Length, Is.EqualTo(200 * 100 * 4));
        renderer.RestartAudio(new(new Domain(30, 12, 8), 60, 1));
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
        debug.RestartAudio(new(new Domain(30, 12, bands), 24, 1));
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
            shaderDirectory: shaders.Path, timeProvider: clock, profile: profile);
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
    // Allow at least 20 seconds of natural decay. Slow simulations retain the
    // original generous upper bound, but dark scenes need not render it all.
    const int MinimumDecayFrames = 1200, DecayFrames = 10000, FinalFrames = 60;

    [Test, Category("GPU")]
    public void SpectrumComparisonShowsLogLeftAndComplexRightFromTheSameStereoAverage()
    {
        const int width = 128, height = 72;
        using var shaders = new SceneShaders("Spectrum");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, width, height, Bands, 100,
            shaderDirectory: shaders.Path, timeProvider: clock);
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
            shaderDirectory: shaders.Path, timeProvider: clock);
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

    public static IEnumerable<TestCaseData> Scenes() =>
        Directory.EnumerateFiles(Path.Combine(ShaderDirectory, "Scenes"), "*.hlsl")
            .Where(path => !IsAudioIndependentPrototype(Path.GetFileNameWithoutExtension(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => new[] { 0.0, 60.0 }.SelectMany(gainDb =>
                (IsFluidScene(Path.GetFileNameWithoutExtension(path)) ? new[] { false, true } : new[] { false })
                    .Select(periodic => new TestCaseData(Path.GetFileNameWithoutExtension(path), gainDb, periodic))));

    static bool IsFluidScene(string name) => name is "MandelFluid" or "MandelFluidv2" or "MandelFluidv3"
        or "FluidFlight" or "PlaneFlight" or "MandelFluidv4";

    // AGENTS.md explicitly exempts audio-independent volume prototypes. Keep
    // production scene discovery and its full natural-decay checks intact.
    static bool IsAudioIndependentPrototype(string name) => name is "MandelVolume" or "MandelLightv2";

    [TestCaseSource(nameof(Scenes)), Category("GPU")]
    public void EverySceneEventuallyTurnsBlackWithoutMusic(string scene, double inputGainDb, bool periodic)
        => CheckSilence(scene, inputGainDb, periodic, restartAudio: false);

    [TestCase("Debug"), TestCase("Histogram"), TestCase("Mandelbox"), TestCase("MandelFluidv4"), Category("GPU")]
    public void AnalysisRestartPreservesSilenceDecayOfRetainedGraphics(string scene)
        => CheckSilence(scene, 0, true, restartAudio: true);

    void CheckSilence(string scene, double inputGainDb, bool periodic, bool restartAudio)
    {
        using var shaders = new SceneShaders(scene);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
        renderer.SelectScene(scene);
        if (IsFluidScene(scene))
        {
            var parameters = renderer.ActiveShader!.Parameters!;
            parameters.SetBoolean(parameters.Items.Single(p => p.Name == "Fluid").Children
                .Single(p => p.Name == "PeriodicBoundary"), periodic);
            if (restartAudio && scene == "MandelFluidv4")
                parameters.SetBoolean(parameters.Items.Single(p => p.Name == "Fluid").Children
                    .Single(p => p.Name == "LowQuality"), true);
        }
        var gain = Math.Pow(10, inputGainDb / 20);
        long row = 0;

        // Test both silent startup and retained light after a real musical input.
        Tick();
        AssertBlack("silent startup");
        foreach (bool sendSilentPackets in new[] { false, true })
        {
            bool lit = false;
            for (int frame = 0; frame < 120; frame++)
            {
                if ((scene is "Mandelbox" or "MandelFluidv2" or "MandelFluidv3") && frame == 30)
                {
                    // A dropped input row followed by analysis arriving late.
                    // Keep this same history alive through recovery and silence.
                    row++;
                    for (int gap = 0; gap < 5; gap++) Tick();
                }
                Post(true);
                Tick();
                if (frame % 20 == 19)
                    lit |= renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Any(v => v > 10);
            }
            Assert.That(lit, Is.True, $"{scene}: musical fixture never produced visible light.");

            if (restartAudio)
            {
                var program = renderer.ActiveShader!.Program.Pixel;
                renderer.RestartAudio(new(new Domain(30, 12, Bands), 60, 1));
                Assert.That(renderer.ActiveShader.Program.Pixel, Is.SameAs(program));
            }

            // Keep the same renderer, scene and simulation. No resets, clearing,
            // large dt jumps or scene switches that could erase retained simulation state.
            for (int frame = 0; frame < DecayFrames; frame++)
            {
                if (sendSilentPackets) Post(false);
                Tick();
                if (frame + 1 >= MinimumDecayFrames && (frame + 1) % 120 == 0
                    && renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).All(v => v == 0))
                    break;
                if (frame % 1000 == 999)
                {
                    // Readback bounds the GPU queue without changing its state.
                    renderer.ReadPixels(false);
                    TestContext.Progress.WriteLine($"{scene}: {(sendSilentPackets ? "zero packets" : "capture stopped")}, decay {frame + 1}/{DecayFrames}");
                }
            }
            for (int frame = 0; frame < FinalFrames; frame++)
            {
                if (sendSilentPackets) Post(false);
                Tick();
                AssertBlack($"{(sendSilentPackets ? "zero packets" : "capture stopped")}, final frame {frame}");
            }
        }

        void Tick() { clock.Advance(); renderer.Render(false); }
        void Post(bool music)
        {
            var samples = new double[Bands * 8];
            if (music)
                for (int i = 0; i < samples.Length; i += 4)
                {
                    samples[i] = gain * .6;
                    samples[i + 1] = gain * (row % 12 < 3 ? .8 : .1);
                    samples[i + 2] = gain * .3;
                    samples[i + 3] = gain * .9;
                }
            renderer.PostAudio(new SampleEvent<double>(samples, Bands * Splitter.OutputStride, DateTime.UnixEpoch)
                {
                    AnalysisRow = row++,
                    ResidualRms = Enumerable.Repeat(music ? gain * .4 : 0, Bands * 2).ToArray(),
                    MomentaryResidual = Enumerable.Repeat(music ? -gain * .4 : 0, Bands * 2).ToArray(),
                }, 2);
        }
        void AssertBlack(string stage)
        {
            var pixels = renderer.ReadPixels(false);
            int nonblack = 0, maximum = 0;
            for (int i = 0; i < pixels.Length; i++)
                if (i % 4 != 3 && pixels[i] != 0) { nonblack++; maximum = Math.Max(maximum, pixels[i]); }
            Assert.That(nonblack, Is.Zero,
                $"{scene}: {stage}: {nonblack} nonzero RGB channels, peak {maximum}/255. Expected exact black.");
        }
    }

    public static IEnumerable<TestCaseData> SmootherSilenceCases() =>
        from scene in new[] { "Spectrum", "Histogram", "Debug", "MandelFluidv3" }
        from gain in new[] { 0.0, 60.0 }
        from swift in new[] { false, true }
        from mode in new[] { SmoothingMode.LogAmplitude, SmoothingMode.Complex }
        select new TestCaseData(scene, gain, swift, mode);

    [TestCaseSource(nameof(SmootherSilenceCases)), Category("GPU")]
    public void SpectrumSmootherAndSplitterEventuallyRenderBlack(string scene, double gainDb, bool useSwift, SmoothingMode mode)
    {
        const int sampleRate = 12000, block = sampleRate / 60;
        var domain = new Domain(55, 12, Bands);
        var options = new AnalyzerOptions { TimeResolution = 60, InputGainDb = gainDb, UseSwift = useSwift };
        var spectrum = SpectrumProcessor.Create(domain, sampleRate, options);
        var smoother = new SpectrumSmoother(spectrum.Frequencies, 1.0 / 60, options);
        var splitter = new Splitter(domain, options);
        using var shaders = new SceneShaders(scene);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
        renderer.SelectScene(scene);
        var input = new double[block]; var observations = new Complex[Bands];
        var output = new double[Bands * 4];
        var a = new double[Bands]; var b = new double[Bands]; var raw = new double[Bands];
        long row = 0;
        var gain = Math.Pow(10, gainDb / 20);
        Tick(false);
        AssertBlack();
        bool lit = false;
        for (int frame = 0; frame < 120; ++frame)
        {
            Tick(true);
            if (frame % 20 == 19) lit |= renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Any(v => v > 10);
        }
        Assert.That(lit, Is.True, "Spectrum/smoother/splitter fixture must excite the scene before silence.");
        int decay = 0;
        for (; decay < DecayFrames; ++decay)
        {
            Tick(false);
            if (decay + 1 >= MinimumDecayFrames && (decay + 1) % 120 == 0
                && renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).All(v => v == 0)) break;
            if (decay % 1000 == 999) renderer.ReadPixels(false);
        }
        for (int frame = 0; frame < FinalFrames; ++frame) { Tick(false); AssertBlack(); }
        Assert.That(observations, Is.All.EqualTo(Complex.Zero), "Actual backend state must expire, not merely render below visibility.");
        Assert.That(output.All(double.IsFinite), Is.True);
        Assert.That(output.Max(Math.Abs), Is.LessThan(1e-9), "The complex model decays naturally rather than gating zero observations.");
        if (scene == "Spectrum")
        {
            var parameters = renderer.ActiveShader!.Parameters!;
            parameters.SetBoolean(parameters.Items.Single(p => p.Name == "CompareSmoothers"), true);
            for (int frame = 0; frame < FinalFrames; ++frame) { Tick(false); AssertBlack(); }
        }

        void Tick(bool active)
        {
            for (int i = 0; i < block; ++i)
            {
                double t = (row * block + i) / (double)sampleRate;
                input[i] = active ? gain * (.6 * Math.Sin(2 * Math.PI * 55 * t)
                    + .3 * Math.Sin(2 * Math.PI * 110 * t)) : 0;
            }
            spectrum.Process(input);
            spectrum.OutputComplex(observations);
            smoother.Process(observations, a, b);
            splitter.ProcessLinear(mode == SmoothingMode.LogAmplitude ? a : b, output);
            for (int k = 0; k < Bands; k++) raw[k] = observations[k].Magnitude;
            renderer.PostAudio(new SampleEvent<double>(output, Bands * 4, DateTime.UnixEpoch)
                { AnalysisRow = row++, ObservedAmplitude = raw, LogSmoothedAmplitude = a, ComplexSmoothedAmplitude = b }, 1);
            clock.Advance(); renderer.Render(false);
        }
        void AssertBlack() => Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero,
            $"{scene}, Swift={useSwift}, {mode}, gain {gainDb} dB, row {row}: every RGB channel must be black.");
    }

    // Compile only the scene under test. Discovery and all decay/RGB assertions stay unchanged;
    // recompiling every unrelated scene for each gain/boundary case dominated this regression.
    sealed class SceneShaders : IDisposable
    {
        readonly string temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        public string Path { get; }
        public SceneShaders(string scene)
        {
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

    [TestCase(960, 540, false), TestCase(540, 960, false), TestCase(1024, 256, false), TestCase(256, 256, false)]
    [TestCase(960, 540, true), TestCase(540, 960, true), TestCase(1024, 256, true), TestCase(256, 256, true), Category("GPU")]
    public void FluidProjectionRemovesDivergenceAndPreservesCurl(int width, int height, bool periodic)
    {
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, width, height, Bands, 60,
            shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene("MandelFluidv3");
        var parameters = renderer.ActiveShader!.Parameters!;
        parameters.SetBoolean(parameters.Items.Single(p => p.Name == "Fluid").Children
            .Single(p => p.Name == "PeriodicBoundary"), periodic);
        clock.Advance(); renderer.Render(false);
        Assert.That(Math.Max(renderer.Fluid.Width, renderer.Fluid.Height), Is.EqualTo(512));
        var check = renderer.Fluid.CheckProjection(renderer.FluidPrograms);
        TestContext.Progress.WriteLine($"{width}x{height}, periodic={periodic}: divergence {check.Before:G5} -> {check.After:G5}, relative curl error {check.CurlError:G5}");
        Assert.That(check.Before, Is.GreaterThan(.01), "The fixture needs a divergent velocity field.");
        Assert.That(check.After, Is.LessThan(check.Before * .01), "Remove at least 99% of RMS divergence.");
        Assert.That(check.CurlError, Is.LessThan(1e-4), "Pressure projection must preserve rotational motion.");
    }

    [TestCase(false), TestCase(true), Category("GPU")]
    public void MandelFluidV4ProjectionPreservesRotation(bool periodic)
    {
        using var shaders = new SceneShaders("MandelFluidv4");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
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
        using var shaders = new SceneShaders("MandelFluidv4");
        using var window = new HiddenWindow();
        var clock = new FluidClock(rate);
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
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
        using var shaders = new SceneShaders("MandelFluidv4");
        using var window = new HiddenWindow();
        var clock = new FluidClock(1000) { Frame = 1000 };
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, analysisRate,
            shaderDirectory: shaders.Path, timeProvider: clock);
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
        using var shaders = new SceneShaders("MandelFluidv4");
        using var window = new HiddenWindow();
        var clock = new FluidClock(rate);
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 1000,
            shaderDirectory: shaders.Path, timeProvider: clock);
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
        using var shaders = new SceneShaders("MandelFluidv4");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
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
        using var shaders = new SceneShaders("MandelFluidv4");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
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
        using var shaders = new SceneShaders("MandelFluidv4");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
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

    [TestCase("MandelFluid"), TestCase("MandelFluidv2"), TestCase("MandelFluidv3"), Category("GPU")]
    public void PeriodicFluidReturnsPigmentAcrossTheSeamAndKeepsItsAge(string scene)
    {
        using var window = new HiddenWindow();
        var open = Measure(false);
        var loop = Measure(true);
        TestContext.Progress.WriteLine($"{scene}: pigment at opposite edge {open.Left:G5} -> {loop.Left:G5}, retained fraction {loop.Fraction:G5}");
        Assert.That(loop.Left, Is.GreaterThan(Math.Max(.01, 10 * open.Left)));
        Assert.That(loop.Fraction, Is.InRange(.6, 1.05), "Periodic transport retains dye apart from configured decay and numerical mixing.");
        (double Left, double Fraction) Measure(bool periodic)
        {
            var clock = new RenderClock();
            using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
                shaderDirectory: ShaderDirectory, timeProvider: clock);
            renderer.SelectScene(scene);
            var parameters = renderer.ActiveShader!.Parameters!;
            var fluid = parameters.Items.Single(p => p.Name == "Fluid");
            var boundary = fluid.Children.Single(p => p.Name == "PeriodicBoundary");
            parameters.SetBoolean(boundary, periodic);
            foreach (var name in new[] { "Flow", "Curl", "Diffusion" })
                parameters.SetFloat(fluid.Children.Single(p => p.Name == name), 0);
            parameters.SetFloat(fluid.Children.Single(p => p.Name == "HalfLife"), .4f);
            clock.Advance(); renderer.Render(false);
            renderer.Fluid.SeedRecirculationForCheck();
            var initial = renderer.Fluid.ReadDye().Where((_, i) => i % 4 == 3).Sum(v => (double)v);
            for (int frame = 0; frame < 60; frame++) { clock.Advance(); renderer.Render(false); }
            var dye = renderer.Fluid.ReadDye();
            Assert.That(dye.All(v => float.IsFinite(v) && v >= 0 && v <= 12), Is.True);
            Assert.That(renderer.Fluid.ReadVelocity().All(float.IsFinite), Is.True);
            double left = 0, total = 0;
            for (int y = 0; y < renderer.Fluid.Height; y++)
                for (int x = 0; x < renderer.Fluid.Width; x++)
                {
                    var density = dye[4 * (y * renderer.Fluid.Width + x) + 3];
                    total += density;
                    if (x < renderer.Fluid.Width / 4) left += density;
                }
            if (periodic && scene == "MandelFluidv3")
            {
                var age = renderer.Fluid.ReadAge();
                for (int cell = 0; cell < age.Length / 2; cell++)
                    if (age[2 * cell + 1] > .001)
                        Assert.That(age[2 * cell] / age[2 * cell + 1], Is.EqualTo(1.5).Within(.03),
                            "Residence age must travel with wrapped pigment.");
            }
            // Changing the option is live and must not reset the simulation.
            parameters.SetBoolean(boundary, !periodic);
            renderer.Render(false);
            Assert.That(renderer.Fluid.PeriodicBoundary, Is.EqualTo(!periodic));
            Assert.That(renderer.Fluid.ReadDye(), Is.EqualTo(dye));
            return (left, total / initial);
        }
    }

    [Test, Category("GPU")]
    public void PeriodicPigmentDecaysThroughTheFloat16SubnormalRange()
    {
        var small = (Half).00002f;
        Assert.That((Half)((float)small * MathF.Exp(-MathF.Log(2) / (10 * 60))), Is.EqualTo(small),
            "The fixture must exercise the exponential rounding plateau.");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene("MandelFluid");
        var parameters = renderer.ActiveShader!.Parameters!;
        var fluid = parameters.Items.Single(p => p.Name == "Fluid");
        parameters.SetBoolean(fluid.Children.Single(p => p.Name == "PeriodicBoundary"), true);
        foreach (var name in new[] { "Flow", "Curl", "Diffusion" })
            parameters.SetFloat(fluid.Children.Single(p => p.Name == name), 0);
        parameters.SetFloat(fluid.Children.Single(p => p.Name == "HalfLife"), 1);
        clock.Advance(); renderer.Render(false);
        renderer.Fluid.SeedRecirculationForCheck(.00002f, 0);
        Assert.That(renderer.Fluid.ReadDye().Max(), Is.GreaterThan(0));
        for (int frame = 0; frame < 360; frame++) { clock.Advance(); renderer.Render(false); }
        Assert.That(renderer.Fluid.ReadDye(), Is.All.Zero,
            "A closed domain must not retain immortal half-float pigment.");
    }

    [Test, Category("GPU")]
    public void MandelFluidV3BackgroundDrivesLocalEmissionAndRotation()
    {
        using var window = new HiddenWindow();
        var off = Measure(0);
        var on = Measure(.7f);
        Assert.That(off.Dye, Is.Zero, "Disabling B sensitivity must suppress background-only injection.");
        Assert.That(off.Motion, Is.Zero);
        Assert.That(on.Dye, Is.GreaterThan(.01), "B must emit material without sustained or transient input.");
        Assert.That(on.Motion, Is.GreaterThan(1e-5), "The same local B must stir the velocity field.");
        Assert.That(on.Right, Is.GreaterThan(3 * on.Left), "A right-channel mid-band source must emit locally.");
        TestContext.Progress.WriteLine($"B response: pigment {off.Dye:G5} -> {on.Dye:G5}, motion {on.Motion:G5}, right/left {on.Right / Math.Max(1e-20, on.Left):G5}");
        (double Dye, double Motion, double Left, double Right) Measure(float background)
        {
            var clock = new RenderClock();
            using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
                shaderDirectory: ShaderDirectory, timeProvider: clock);
            renderer.SelectScene("MandelFluidv3");
            var parameters = renderer.ActiveShader!.Parameters!;
            parameters.SetFloat(parameters.Items.Single(p => p.Name == "Fluid").Children.Single(p => p.Name == "Background"), background);
            parameters.SetFloat(parameters.Items.Single(p => p.Name == "Quality").Children.Single(p => p.Name == "MovementSpeed"), 0);
            var data = new double[Bands * 8];
            for (int band = Bands / 2 - 4; band <= Bands / 2 + 4; band++)
            {
                int i = 4 * (Bands + band);
                data[i + 2] = .8;
                data[i + 3] = .8;
            }
            for (int frame = 0; frame < 8; frame++)
            {
                renderer.PostAudio(new SampleEvent<double>(data, Bands * Splitter.OutputStride, DateTime.UnixEpoch)
                    { AnalysisRow = frame }, 2);
                clock.Advance(); renderer.Render(false);
            }
            var dye = renderer.Fluid.ReadDye();
            var velocity = renderer.Fluid.ReadVelocity();
            Assert.That(dye.All(v => float.IsFinite(v) && v >= 0 && v <= 12), Is.True);
            Assert.That(velocity.All(float.IsFinite), Is.True);
            double left = 0, right = 0;
            int w = renderer.Fluid.Width, h = renderer.Fluid.Height;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float density = dye[4 * (y * w + x) + 3];
                    if (x < w / 2) left += density; else right += density;
                }
            return (left + right, Math.Sqrt(velocity.Average(v => (double)v * v)), left, right);
        }
    }

    [Test, Category("GPU")]
    public void MandelFluidV3CurlBuildsStrongerRotationWithBoundedPigment()
    {
        using var window = new HiddenWindow();
        double gentle = Measure(0), swirling = Measure(1);
        TestContext.Progress.WriteLine($"Curl control: RMS vorticity {gentle:G5} -> {swirling:G5}");
        Assert.That(swirling, Is.GreaterThan(2 * gentle), "Curl must strengthen the actual velocity field.");
        double Measure(float curl)
        {
            var clock = new RenderClock();
            using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
                shaderDirectory: ShaderDirectory, timeProvider: clock);
            renderer.SelectScene("MandelFluidv3");
            var parameters = renderer.ActiveShader!.Parameters!;
            parameters.SetFloat(parameters.Items.Single(p => p.Name == "Fluid").Children.Single(p => p.Name == "Curl"), curl);
            parameters.SetFloat(parameters.Items.Single(p => p.Name == "Quality").Children.Single(p => p.Name == "MovementSpeed"), 0);
            var data = new double[Bands * 8];
            for (int i = 0; i < data.Length; i += 4) { data[i] = .4; data[i + 3] = .4; }
            for (int frame = 0; frame < 120; frame++)
            {
                renderer.PostAudio(new SampleEvent<double>(data, Bands * Splitter.OutputStride, DateTime.UnixEpoch)
                    { AnalysisRow = frame }, 2);
                clock.Advance(); renderer.Render(false);
            }
            var dye = renderer.Fluid.ReadDye();
            Assert.That(dye.All(v => float.IsFinite(v) && v >= 0 && v <= 12), Is.True,
                "Corrected advection must keep every pigment channel finite, positive and bounded.");
            Assert.That(dye.Max(), Is.GreaterThan(.01), "Sustained notes must emit liquid.");
            var v = renderer.Fluid.ReadVelocity();
            Assert.That(v.All(float.IsFinite), Is.True);
            int w = renderer.Fluid.Width, h = renderer.Fluid.Height;
            double power = 0;
            for (int y = 0; y < h - 1; y++)
                for (int x = 0; x < w - 1; x++)
                {
                    int i = 2 * (y * w + x);
                    double rotation = (v[i + 3] - v[i + 1]) / (2 * renderer.Fluid.Aspect / w)
                        - (v[i + 2 * w] - v[i]) / (2.0 / h);
                    power += rotation * rotation;
                }
            return Math.Sqrt(power / ((w - 1) * (h - 1)));
        }
    }

    [Test, Category("GPU")]
    public void MandelFluidV3CentralStrobesRevealOldCloudsWithoutChangingFluid()
    {
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        const int width = 384, height = 216;
        using var renderer = new Renderer(window.Handle, width, height, Bands, 60,
            shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene("MandelFluidv3");
        var parameters = renderer.ActiveShader!.Parameters!;
        Assert.That(parameters.Items.Any(p => p.Name == "Comet"), Is.False);
        var lighting = parameters.Items.Single(p => p.Name == "Lighting");
        Assert.That(lighting.Children.Select(p => p.Name), Is.EquivalentTo(new[] { "Strobe", "Ambient" }));
        var strobe = lighting.Children.Single(p => p.Name == "Strobe");
        var ambient = lighting.Children.Single(p => p.Name == "Ambient");
        Assert.That(parameters.GetFloat(strobe), Is.EqualTo(.7f));
        Assert.That(parameters.GetFloat(ambient), Is.EqualTo(.008f));
        parameters.SetFloat(ambient, 0);
        parameters.SetFloat(parameters.Items.Single(p => p.Name == "Quality")
            .Children.Single(p => p.Name == "MovementSpeed"), 0);
        long row = 0;
        for (int frame = 0; frame < 240; frame++) Tick(Bands / 4);
        parameters.SetFloat(ambient, .008f);
        Tick(toneBand: Bands / 4, advance: false);
        Capture("cloud-bed");
        Tick(toneBand: Bands / 4, attackBand: Bands / 4, advance: false);
        Capture("cloud-strobe");
        parameters.SetFloat(ambient, 0);
        // Let local component envelopes settle as well as the light envelope;
        // otherwise successive zero-dt lighting edits can still change depth.
        for (int frame = 0; frame < 150; frame++) Tick();
        double initialAge = MeanAge();
        for (int frame = 0; frame < 30; frame++) Tick();
        Assert.That(MeanAge() - initialAge, Is.EqualTo(.5).Within(.025));
        Assert.That(renderer.Fluid.ReadDye().Max(), Is.GreaterThan(.01));
        AssertBlack("Retained dye is dark without transient illumination when ambient is zero.");
        var pigment = renderer.Fluid.ReadDye();
        var velocity = renderer.Fluid.ReadVelocity();
        Tick(attackBand: Bands / 4, advance: false);
        long flashLight = Light();
        var flashImage = renderer.ReadPixels(false);
        Capture("old-cloud-strobe");
        Tick(attackBand: 3 * Bands / 4, advance: false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(flashImage),
            "A central strobe reveals the whole cloud, independent of the attack's spectral color.");
        Assert.That(flashLight, Is.GreaterThan(1000), "Current transients must reveal old material immediately.");
        Assert.That(renderer.Fluid.ReadDye(), Is.EqualTo(pigment), "Lighting must not change pigment transport.");
        Assert.That(renderer.Fluid.ReadVelocity(), Is.EqualTo(velocity), "Lighting must not change the fluid motion.");
        renderer.Fluid.AddAgeForCheck(40);
        Tick(attackBand: Bands / 4, advance: false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(flashImage),
            "Residence age must not suppress a new strobe on returning material.");
        parameters.SetFloat(strobe, 0);
        Tick(attackBand: Bands / 4, advance: false);
        AssertBlack("The Strobe control must disable transient light.");
        parameters.SetFloat(strobe, .7f);
        for (int frame = 0; frame < 45; frame++) Tick();
        AssertBlack("The cloud must go dark between hits while pigment remains.");
        Assert.That(renderer.Fluid.ReadDye().Max(), Is.GreaterThan(.001));
        parameters.SetFloat(strobe, 0);
        parameters.SetFloat(ambient, .008f);
        Tick(advance: false);
        long ambientLight = Light();
        Assert.That(ambientLight, Is.GreaterThan(0), "Ambient can gently reveal retained dye.");
        Assert.That(ambientLight, Is.LessThan(flashLight / 3), "Default ambient must preserve dramatic strobe contrast.");
        parameters.SetFloat(ambient, .04f);
        Tick(advance: false);
        Assert.That(Light(), Is.GreaterThan(ambientLight), "Ambient intensity must remain configurable.");
        renderer.Fluid.Clear();
        parameters.SetFloat(strobe, .7f);
        Tick(attackBand: Bands / 4, advance: false);
        AssertBlack("Neither ambient nor a strobe may illuminate empty space.");

        long Light() => renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Sum(v => (long)v);
        double MeanAge()
        {
            var ages = renderer.Fluid.ReadAge();
            Assert.That(ages.All(v => float.IsFinite(v) && v >= 0), Is.True);
            double moment = 0, mass = 0;
            for (int i = 0; i < ages.Length; i += 2) { moment += ages[i]; mass += ages[i + 1]; }
            Assert.That(mass, Is.GreaterThan(.01));
            return moment / mass;
        }
        void AssertBlack(string message) => Assert.That(
            renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).All(v => v == 0), Is.True, message);
        void Capture(string name)
        {
            var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "mandelfluidv3");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name + ".rgba");
            File.WriteAllBytes(path, renderer.ReadPixels(false));
            TestContext.AddTestAttachment(path, $"{width}x{height} RGBA8 cloud lighting");
        }
        void Tick(int toneBand = -1, int attackBand = -1, bool advance = true)
        {
            var data = new double[Bands * 8];
            for (int i = 0; i < data.Length; i += 4)
            {
                int band = i / 4 % Bands;
                data[i] = toneBand >= 0 && (Math.Abs(band - toneBand) <= 2
                    || Math.Abs(band - 2 * toneBand) <= 2 || Math.Abs(band - 3 * toneBand) <= 2) ? .6 : 0;
                data[i + 1] = attackBand >= 0 && Math.Abs(band - attackBand) <= 2 ? .8 : 0;
                data[i + 2] = toneBand >= 0 ? .2 : 0;
                data[i + 3] = Math.Max(data[i + 2], Math.Max(data[i], data[i + 1]));
            }
            renderer.PostAudio(new SampleEvent<double>(data, Bands * Splitter.OutputStride, DateTime.UnixEpoch)
                { AnalysisRow = row++ }, 2);
            if (advance) clock.Advance();
            renderer.Render(false);
        }
    }
    [Test, Category("GPU")]
    public void MandelFluidV3TransientsWithoutPigmentStayBlack()
    {
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene("MandelFluidv3");
        long row = 0;
        Tick(false);
        Tick(true);
        var hit = renderer.ReadPixels(false);
        Assert.That(hit.Where((_, i) => i % 4 != 3).All(v => v == 0), Is.True,
            "A transient alone must leave every RGB channel black.");
        for (int i = 0; i < 12; i++)
        {
            Tick(false);
            Assert.That(renderer.ReadPixels(false).Where((_, c) => c % 4 != 3).All(v => v == 0), Is.True,
                "Once the current transient is zero, retained history must not light a delayed front.");
        }
        Assert.That(renderer.Fluid.ReadDye().All(v => v == 0), Is.True, "Transients cannot inject liquid.");
        void Tick(bool hit)
        {
            var data = new double[Bands * 8];
            if (hit)
                for (int channel = 0; channel < 2; channel++)
                    for (int band = 0; band < Bands; band++)
                    {
                        int i = 4 * (channel * Bands + band);
                        data[i + 1] = .8 * Math.Exp(-.5 * Math.Pow((band - Bands / 2.0) / 1.5, 2));
                        data[i + 3] = data[i + 1];
                    }
            renderer.PostAudio(new SampleEvent<double>(data, Bands * Splitter.OutputStride, DateTime.UnixEpoch)
                { AnalysisRow = row++ }, 2);
            clock.Advance(); renderer.Render(false);
        }
    }

    [Test, Category("GPU")]
    public void TransientCutoutDistortionIsSpectralAndReversible()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var shaders = Path.Combine(temporaryRoot, "AEther-cutout-pulse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(shaders, "Common"));
        Directory.CreateDirectory(Path.Combine(shaders, "Scenes"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(ShaderDirectory, "Common"), "*.hlsl"))
                File.Copy(file, Path.Combine(shaders, "Common", Path.GetFileName(file)));
            // Render the production cutout as a binary silhouette, isolating
            // its geometry from flashes, fluid memory and exposure changes.
            var source = File.ReadAllText(Path.Combine(ShaderDirectory, "Scenes", "MandelFluidv2.hlsl"));
            const string entry = "float4 PS(const PSin input)";
            Assert.That(source, Does.Contain(entry));
            source = source.Replace(entry, "float4 OriginalPS(const PSin input)") + """

                float4 PS(const PSin input) : SV_Target
                {
                    float2 p = (2 * input.UV - 1) * float2(AspectRatio, 1);
                    float outside = step(CometBoundary(), CometDistances(p).x);
                    return float4(outside, outside, outside, 1);
                }
                """;
            File.WriteAllText(Path.Combine(shaders, "Scenes", "CutoutPulseProbe.hlsl"), source);
            using var window = new HiddenWindow();
            var clock = new RenderClock();
            using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
                shaderDirectory: shaders, timeProvider: clock);
            // Hold the ongoing tumble still to isolate the deformation, and
            // settle the tonal/noise material at a useful fractal depth.
            var parameters = renderer.ActiveShader!.Parameters!;
            parameters.SetFloat(parameters.Items.Single(p => p.Name == "Quality")
                .Children.Single(p => p.Name == "MovementSpeed"), 0);
            long row = 0;
            for (int frame = 0; frame < 1200; frame++) Tick();
            var original = renderer.ReadPixels(false);
            int originalArea = Area(original);
            Assert.That(originalArea, Is.GreaterThan(10), "The fixture must contain a visible cutout.");

            Tick(Bands / 4);
            var lowHit = renderer.ReadPixels(false);
            Assert.That(lowHit, Is.Not.EqualTo(original), "A low-band hit must deform the cutout.");
            for (int frame = 0; frame < 90; frame++) Tick();
            Assert.That(renderer.ReadPixels(false), Is.EqualTo(original),
                "After the deformation, every pixel must return to the underlying silhouette.");
            Tick(3 * Bands / 4);
            var highHit = renderer.ReadPixels(false);
            Assert.That(highHit, Is.Not.EqualTo(original), "A high-band hit must deform the cutout.");
            Assert.That(highHit, Is.Not.EqualTo(lowHit), "Different transient bands must deform different regions.");
            for (int frame = 0; frame < 90; frame++) Tick();
            Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));

            void Tick(int attackBand = -1)
            {
                var samples = new double[Bands * 8];
                for (int i = 0; i < samples.Length; i += 4)
                {
                    int band = i / 4 % Bands;
                    samples[i] = .6;
                    samples[i + 1] = attackBand >= 0 && Math.Abs(band - attackBand) <= 4 ? .8 : 0;
                    samples[i + 2] = .3;
                    samples[i + 3] = .9;
                }
                renderer.PostAudio(new SampleEvent<double>(samples, Bands * Splitter.OutputStride, DateTime.UnixEpoch)
                    { AnalysisRow = row++ }, 2);
                clock.Advance(); renderer.Render(false);
            }
            static int Area(byte[] pixels) => Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[4 * i] == 0);
        }
        finally
        {
            if (!Path.GetFullPath(shaders).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cutout probe cleanup escaped the temporary directory.");
            Directory.Delete(shaders, true);
        }
    }

    [TestCase("Spectrum", 0.0), TestCase("Spectrum", 60.0)]
    [TestCase("Debug", 0.0), TestCase("Debug", 60.0)]
    [TestCase("Histogram", 0.0), TestCase("Histogram", 60.0), Category("GPU")]
    public async Task WaveformThroughAnalysisViewsEventuallyTurnsBlack(string scene, double gainDb)
    {
        const int sampleRate = 8000, batch = sampleRate / 60;
        var options = new AnalyzerOptions
        {
            MinFrequency = 100, MaxFrequency = 1000, FrequencyResolution = 4,
            TimeResolution = 60, InputGainDb = gainDb,
        };
        var analyzer = new Analyzer(new(SampleType.Float32.Instance, sampleRate, 2), options);
        var rows = new List<SampleEvent<double>>();
        analyzer.SamplesAnalyzed += (_, e) => rows.Add(e with
            { Samples = e.Samples.AsSpan(0, e.SampleCount * 2).ToArray(), ResidualRms = e.ResidualRms.ToArray() });
        var waveform = new float[140 * batch * 2];
        for (int j = 0; j < 20 * batch; j++)
        {
            waveform[2 * j] = (float)(.4 * Math.Sin(j * .17));
            waveform[2 * j + 1] = -waveform[2 * j];
        }
        var bytes = new byte[waveform.Length * sizeof(float)];
        Buffer.BlockCopy(waveform, 0, bytes, 0, bytes.Length);
        analyzer.PostSamples(bytes); analyzer.Stop();
        await analyzer.Completion;
        Assert.That(rows.Count, Is.EqualTo(140));
        Assert.That(rows[^1].Samples, Is.All.Zero);

        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, options.Domain.Length,
            sampleRate / (double)batch, shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene(scene);
        bool lit = false;
        foreach (var row in rows)
        {
            renderer.PostAudio(row, 2);
            Tick();
            if (row.AnalysisRow == 19)
                lit = renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Any(v => v > 10);
        }
        Assert.That(lit, Is.True, "The production waveform fixture must excite the view.");
        for (int frame = 0; frame < MinimumDecayFrames; frame++) Tick();
        for (int frame = 0; frame < FinalFrames; frame++)
        {
            Tick();
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero,
                $"Production waveform/spectrum/local fit: final frame {frame}.");
        }
        void Tick() { clock.Advance(); renderer.Render(false); }
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
        public IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "AEther shader silence regression",
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public class ShaderSilenceTests
{
    const int Width = 96, Height = 54, Bands = 32;
    // Allow at least 20 seconds of natural decay. Slow simulations retain the
    // original generous upper bound, but dark scenes need not render it all.
    const int MinimumDecayFrames = 1200, DecayFrames = 10000, FinalFrames = 60;

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
            // Explicitly audio-independent study scene; covered separately below.
            .Where(path => Path.GetFileNameWithoutExtension(path) != "Mandelbench")
            .OrderBy(path => path, StringComparer.Ordinal)
            .SelectMany(path => new[] { 0.0, 60.0 }.Select(gainDb =>
                new TestCaseData(Path.GetFileNameWithoutExtension(path), gainDb)));

    [TestCaseSource(nameof(Scenes)), Category("GPU")]
    public void EverySceneEventuallyTurnsBlackWithoutMusic(string scene, double inputGainDb)
    {
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene(scene);
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
                if ((scene is "Mandelbox" or "MandelFluidv2") && frame == 30)
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

            // Keep the same renderer, scene and simulation. No resets, clearing,
            // large dt jumps or scene switches that could erase retained state.
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
                { AnalysisRow = row++ }, 2);
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

    [TestCase(false), TestCase(true), Category("GPU")]
    public void MandelbenchIsVisibleAndIndependentOfAudio(bool julia)
    {
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene("Mandelbench");
        var parameters = renderer.ActiveShader!.Parameters!;
        parameters.SetBoolean(parameters.Items.Single(p => p.Name == "IFS")
            .Children.Single(p => p.Name == "Juliabox"), julia);
        clock.Advance();
        renderer.Render(false);
        var original = renderer.ReadPixels(false);
        Assert.That(original.Where((_, i) => i % 4 != 3).Any(v => v > 10), Is.True);
        foreach (bool music in new[] { true, false })
        {
            for (int frame = 0; frame < 12; frame++)
            {
                var samples = Enumerable.Repeat(music ? .8 : 0, Bands * 8).ToArray();
                renderer.PostAudio(new SampleEvent<double>(samples, Bands * Splitter.OutputStride, DateTime.UnixEpoch)
                    { AnalysisRow = (music ? 0 : 120) + frame }, 2);
                clock.Advance();
                renderer.Render(false);
            }
            Assert.That(renderer.ReadPixels(false), Is.EqualTo(original),
                "Mandelbench must remain static through music and silence.");
        }
        {
            var diagnostics = parameters.Items.Single(p => p.Name == "Jacobian");
            var view = parameters.Items.Single(p => p.Name == "View");
            parameters.SetBoolean(diagnostics.Children.Single(p => p.Name == "Enabled"), true);
            for (int mode = 0; mode < 5; mode++)
            {
                parameters.SetBoolean(diagnostics.Children.Single(p => p.Name == "MatrixEntries"), mode == 1);
                parameters.SetBoolean(diagnostics.Children.Single(p => p.Name == "BoundRatio"), mode == 2);
                parameters.SetBoolean(view.Children.Single(p => p.Name == "DistanceContours"), mode >= 3);
                parameters.SetBoolean(view.Children.Single(p => p.Name == "Antialiasing"), mode == 4);
                clock.Advance();
                renderer.Render(false);
                var diagnosticImage = renderer.ReadPixels(false);
                Assert.That(diagnosticImage, Is.Not.EqualTo(original), $"Diagnostic mode {mode} must replace the default bands.");
                Assert.That(diagnosticImage.Where((_, i) => i % 4 != 3).Any(v => v > 10), Is.True);
                clock.Advance();
                renderer.Render(false);
                Assert.That(renderer.ReadPixels(false), Is.EqualTo(diagnosticImage));
            }
        }
    }

    [TestCase(false, 0f), TestCase(false, .15f), TestCase(false, .5f), TestCase(false, .85f), TestCase(false, 1f)]
    [TestCase(true, 0f), TestCase(true, .15f), TestCase(true, .5f), TestCase(true, .85f), TestCase(true, 1f)]
    [Category("GPU")]
    public void MandelbenchJacobianMatchesFiniteDifferences(bool julia, float step)
    {
        var shaders = Path.Combine(Path.GetTempPath(), "AEther-jacobian-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(shaders, "Common"));
        Directory.CreateDirectory(Path.Combine(shaders, "Scenes"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(ShaderDirectory, "Common"), "*.hlsl"))
                File.Copy(file, Path.Combine(shaders, "Common", Path.GetFileName(file)));
            var source = File.ReadAllText(Path.Combine(ShaderDirectory, "Scenes", "Mandelbench.hlsl"));
            source = source.Replace("float4 PS(PSin input)", "float4 OriginalPS(PSin input)") + """

                float4 PS(PSin input) : SV_Target
                {
                    float3 p = float3((input.UV * 2 - 1) * 1.83 + .017, .237);
                    float bound;
                    float3x3 j;
                    OrbitJacobian(p, bound, j);
                    float3x3 numerical = 0, coarse = 0;
                    [unroll] for (int axis = 0; axis < 3; axis++)
                    {
                        float3 e = 0;
                        e[axis] = .0002;
                        float unused;
                        float3 fineColumn = (Orbit(p + e, unused) - Orbit(p - e, unused)) / .0004;
                        e *= 2;
                        float3 coarseColumn = (Orbit(p + e, unused) - Orbit(p - e, unused)) / .0008;
                        numerical[0][axis] = fineColumn.x;
                        numerical[1][axis] = fineColumn.y;
                        numerical[2][axis] = fineColumn.z;
                        coarse[0][axis] = coarseColumn.x;
                        coarse[1][axis] = coarseColumn.y;
                        coarse[2][axis] = coarseColumn.z;
                    }
                    float norm = LargestStretch(j);
                    float error = 0, convergence = 0;
                    [unroll] for (int row = 0; row < 3; row++)
                    {
                        error = max(error, length(j[row] - numerical[row]));
                        convergence = max(convergence, length(coarse[row] - numerical[row]));
                    }
                    float tolerance = .02 * max(1, norm);
                    // Omit points where the finite-difference stencil crosses a crease.
                    bool incorrectJacobian = convergence < tolerance * .25 && error > tolerance;
                    bool incorrectBound = norm > bound * 1.001 + .00001;
                    float3x3 rankOne = float3x3(1,2,3, 2,4,6, 0,0,0);
                    bool incorrectNorm = abs(LargestStretch(rankOne) - sqrt(70.0)) > .0001
                        || abs(LargestStretch(3 * Identity) - 3) > .0001
                        || LargestStretch((float3x3)0) != 0;
                    return float4(incorrectJacobian ? 1 : 0, incorrectBound ? 1 : 0, incorrectNorm ? 1 : 0, 1);
                }
                """;
            File.WriteAllText(Path.Combine(shaders, "Scenes", "JacobianProbe.hlsl"), source);
            using var window = new HiddenWindow();
            using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60, shaderDirectory: shaders);
            var parameters = renderer.ActiveShader!.Parameters!;
            var ifs = parameters.Items.Single(p => p.Name == "IFS");
            parameters.SetBoolean(ifs.Children.Single(p => p.Name == "Juliabox"), julia);
            parameters.SetFloat(ifs.Children.Single(p => p.Name == "Step"), step);
            parameters.SetFloat(ifs.Children.Single(p => p.Name == "Iterations"), 2f / 12);
            renderer.Render(false);
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).All(v => v == 0), Is.True,
                "Red: analytic derivative differs; green: scalar bound violated; blue: singular value solve failed.");
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (!Path.GetFullPath(shaders).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Jacobian probe cleanup escaped the temporary directory.");
            Directory.Delete(shaders, true);
        }
    }

    [Test, Category("GPU")]
    public void MandelbenchSliceDistanceMatchesCircle()
    {
        var shaders = Path.Combine(Path.GetTempPath(), "AEther-distance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(shaders, "Common"));
        Directory.CreateDirectory(Path.Combine(shaders, "Scenes"));
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(ShaderDirectory, "Common"), "*.hlsl"))
                File.Copy(file, Path.Combine(shaders, "Common", Path.GetFileName(file)));
            var source = File.ReadAllText(Path.Combine(ShaderDirectory, "Scenes", "Mandelbench.hlsl"));
            source = source.Replace("float4 PS(PSin input)", "float4 OriginalPS(PSin input)") + """

                float4 PS(PSin input) : SV_Target
                {
                    float2 p = (2 * input.UV - 1) * float2(AspectRatio, 1) * 2;
                    float2 sample = DistanceField.SampleLevel(DistanceSampler, .5 + (input.UV - .5) / 1.5, 0);
                    float exact = length(p) - (2 + 30 * Search.EscapeRadius);
                    float tolerance = 4 / Resolution.y;
                    return float4(sample.y < .99 ? 1 : 0, abs(sample.x - exact) > tolerance ? 1 : 0,
                        abs(exact) > tolerance && sample.x * exact < 0 ? 1 : 0, 1);
                }
                """;
            File.WriteAllText(Path.Combine(shaders, "Scenes", "DistanceProbe.hlsl"), source);
            using var window = new HiddenWindow();
            using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60, shaderDirectory: shaders);
            var parameters = renderer.ActiveShader!.Parameters!;
            var ifs = parameters.Items.Single(p => p.Name == "IFS");
            parameters.SetFloat(ifs.Children.Single(p => p.Name == "Iterations"), 0);
            parameters.SetFloat(ifs.Children.Single(p => p.Name == "Step"), 0);
            parameters.SetFloat(parameters.Items.Single(p => p.Name == "Search").Children.Single(p => p.Name == "EscapeRadius"), 0);
            renderer.Render(false);
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).All(v => v == 0), Is.True,
                "The sampled circle distance must agree with the exact signed distance within one display pixel, inside and outside.");
        }
        finally
        {
            var root = Path.GetFullPath(Path.GetTempPath());
            if (!Path.GetFullPath(shaders).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Distance probe cleanup escaped the temporary directory.");
            Directory.Delete(shaders, true);
        }
    }

    [Test, Category("GPU")]
    public void MandelbenchDistancePreview()
    {
        const int width = 480, height = 300;
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, width, height, Bands, 60, shaderDirectory: ShaderDirectory);
        renderer.SelectScene("Mandelbench");
        var parameters = renderer.ActiveShader!.Parameters!;
        var search = parameters.Items.Single(p => p.Name == "Search");
        var view = parameters.Items.Single(p => p.Name == "View");
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "artifacts", "mandelbench-distance");
        Directory.CreateDirectory(directory);
        var original = Capture("bands");
        parameters.SetBoolean(search.Children.Single(p => p.Name == "ShowFailures"), true);
        var failures = Capture("failures");
        int failed = Enumerable.Range(0, width * height)
            .Count(i => failures[4 * i] > 100 && failures[4 * i + 1] < 30 && failures[4 * i + 2] > 50);
        TestContext.Progress.WriteLine($"Closest-point search failures: {failed}/{width * height} ({100.0 * failed / (width * height):F2}%).");
        Assert.That(failed, Is.Zero, "The default slice must have a propagated boundary sample everywhere in view.");
        parameters.SetBoolean(search.Children.Single(p => p.Name == "ShowFailures"), false);
        parameters.SetBoolean(view.Children.Single(p => p.Name == "DistanceContours"), true);
        foreach (float level in new[] { .25f, .5f, .7f })
        {
            parameters.SetFloat(view.Children.Single(p => p.Name == "DistanceLevel"), level);
            Capture("level-" + level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        parameters.SetBoolean(view.Children.Single(p => p.Name == "DistanceContours"), false);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original), "Appearance edits must preserve the cached distance geometry.");
        var scale = parameters.Items.Single(p => p.Name == "IFS").Children.Single(p => p.Name == "Scale");
        float originalScale = parameters.GetFloat(scale);
        parameters.SetFloat(scale, .25f);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.Not.EqualTo(original), "Geometry edits must rebuild the field.");
        parameters.SetFloat(scale, originalScale);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
        Assert.That(renderer.ReloadShaders(), Is.True, renderer.ShaderError);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original), "Shader reload must invalidate the cached programs.");
        renderer.Resize(320, 200);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Length, Is.EqualTo(320 * 200 * 4));

        byte[] Capture(string name)
        {
            renderer.Render(false);
            var pixels = renderer.ReadPixels(false);
            File.WriteAllBytes(Path.Combine(directory, name + ".rgba"), pixels);
            TestContext.AddTestAttachment(Path.Combine(directory, name + ".rgba"), "480x300 RGBA8 shader readback");
            return pixels;
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

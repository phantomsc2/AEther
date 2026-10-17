using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
        if (scene is "Spectrum" or "Histogram")
        {
            var parameters = renderer.ActiveShader!.Parameters!;
            parameters.SetBoolean(parameters.Items.Single(p => p.Name == "ShowGlobalSTBR"), true);
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
                { AnalysisRow = row++, GlobalEvidence = music
                    ? new GlobalEvidence(gain * .3, gain * (row % 12 < 6 ? .2 : -.2), gain * .5, gain * .1, Bands, 13)
                    : new GlobalEvidence(0, 0, 0, 0, Bands, 13) }, 2);
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

    [TestCase(960, 540), TestCase(540, 960), TestCase(1024, 256), TestCase(256, 256), Category("GPU")]
    public void FluidProjectionRemovesDivergenceAndPreservesCurl(int width, int height)
    {
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, width, height, Bands, 60,
            shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene("MandelFluidv3");
        clock.Advance(); renderer.Render(false);
        Assert.That(Math.Max(renderer.Fluid.Width, renderer.Fluid.Height), Is.EqualTo(512));
        var check = renderer.Fluid.CheckProjection(renderer.FluidPrograms);
        TestContext.Progress.WriteLine($"{width}x{height}: divergence {check.Before:G5} -> {check.After:G5}, relative curl error {check.CurlError:G5}");
        Assert.That(check.Before, Is.GreaterThan(.01), "The fixture needs a divergent velocity field.");
        Assert.That(check.After, Is.LessThan(check.Before * .01), "Remove at least 99% of RMS divergence.");
        Assert.That(check.CurlError, Is.LessThan(1e-4), "Pressure projection must preserve rotational motion.");
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
    public void MandelFluidV3CurrentTransientsActivateOldMatchingPigment()
    {
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene("MandelFluidv3");
        var parameters = renderer.ActiveShader!.Parameters!;
        var lighting = parameters.Items.Single(p => p.Name == "Lighting");
        var fadeTime = lighting.Children.Single(p => p.Name == "FadeTime");
        var ambient = lighting.Children.Single(p => p.Name == "Ambient");
        Assert.That(parameters.GetFloat(fadeTime), Is.EqualTo(.1f));
        Assert.That(parameters.GetFloat(ambient), Is.EqualTo(.04f));
        // Isolate the exponential response at a known, configurable time scale.
        parameters.SetFloat(fadeTime, (2f - .05f) / 3.95f);
        parameters.SetFloat(ambient, 0);
        parameters.SetFloat(parameters.Items.Single(p => p.Name == "Quality")
            .Children.Single(p => p.Name == "MovementSpeed"), 0);
        long row = 0;
        for (int frame = 0; frame < 120; frame++) Tick(Bands / 4);
        // Retain material older than the histogram; age attenuates its response
        // without changing the current spectrum used to activate it.
        for (int frame = 0; frame < 60; frame++) Tick();
        double initialAge = MeanAge();
        for (int frame = 0; frame < 30; frame++) Tick();
        Assert.That(MeanAge() - initialAge, Is.EqualTo(.5).Within(.025));
        Assert.That(renderer.Fluid.ReadDye().Max(), Is.GreaterThan(.01));
        AssertBlack("Retained dye is dark without transient illumination.");
        var pigment = renderer.Fluid.ReadDye();
        var velocity = renderer.Fluid.ReadVelocity();
        Tick(attackBand: Bands / 4, advance: false);
        long matching = Light();
        var youngImage = renderer.ReadPixels(false);
        Tick(attackBand: 3 * Bands / 4, advance: false);
        long unrelated = Light();
        Assert.That(matching, Is.GreaterThan(1000), "Current transients must illuminate old material immediately.");
        Assert.That(matching, Is.GreaterThan(1.3 * unrelated), "Color must select its matching current frequency.");
        Assert.That(renderer.Fluid.ReadDye(), Is.EqualTo(pigment), "Lighting must not change pigment transport.");
        Assert.That(renderer.Fluid.ReadVelocity(), Is.EqualTo(velocity), "Lighting must not change the fluid motion.");
        // Isolate age from pigment decay and motion. Undo gamma and tone mapping
        // to measure the requested exponential factor in actual emitted light.
        renderer.Fluid.AddAgeForCheck(2);
        Tick(attackBand: Bands / 4, advance: false);
        var agedImage = renderer.ReadPixels(false);
        double youngLight = 0, agedLight = 0;
        for (int i = 0; i < youngImage.Length; i++)
            if (i % 4 != 3 && youngImage[i] > 20 && youngImage[i] < 240)
            {
                youngLight += LinearLight(youngImage[i]);
                agedLight += LinearLight(agedImage[i]);
            }
        Assert.That(youngLight, Is.GreaterThan(1));
        Assert.That(agedLight / youngLight, Is.EqualTo(Math.Exp(-1)).Within(.04),
            "Adding two seconds of age must multiply transient illumination by exp(-1).");
        for (int frame = 0; frame < 45; frame++) Tick();
        AssertBlack("The cloud must go dark after current transients release, while pigment remains.");
        Assert.That(renderer.Fluid.ReadDye().Max(), Is.GreaterThan(.001));
        parameters.SetFloat(fadeTime, .1f);
        Tick(attackBand: Bands / 4, advance: false);
        Assert.That(Light(), Is.LessThan(matching / 2), "The faster default must suppress old-material flashes.");
        renderer.Fluid.AddAgeForCheck(40);
        parameters.SetFloat(ambient, .04f);
        Tick(attackBand: Bands / 4, advance: false);
        long ambientLight = Light();
        Assert.That(ambientLight, Is.GreaterThan(1000), "Old material settles to the ambient floor.");
        Tick(advance: false);
        Assert.That(Light(), Is.EqualTo(ambientLight).Within((long)(ambientLight * .02)),
            "Aged ambient illumination must be independent of current transients.");
        parameters.SetFloat(ambient, .08f);
        Tick(advance: false);
        Assert.That(Light(), Is.GreaterThan(ambientLight), "Ambient intensity must be configurable.");
        renderer.Fluid.Clear();
        Tick(attackBand: Bands / 4, advance: false);
        AssertBlack("A transient must not illuminate empty space.");

        long Light() => renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Sum(v => (long)v);
        double MeanAge()
        {
            var ages = renderer.Fluid.ReadAge();
            Assert.That(ages.All(v => float.IsFinite(v) && v >= 0), Is.True);
            double moment = 0, mass = 0;
            for (int i = 0; i < ages.Length; i += 2)
            {
                moment += ages[i]; mass += ages[i + 1];
            }
            Assert.That(mass, Is.GreaterThan(.01));
            return moment / mass;
        }
        static double LinearLight(byte value)
        {
            double linear = Math.Pow(value / 255.0, 2.2);
            return linear / (1 - linear);
        }
        void AssertBlack(string message) => Assert.That(
            renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).All(v => v == 0), Is.True, message);
        void Tick(int toneBand = -1, int attackBand = -1, bool advance = true)
        {
            var data = new double[Bands * 8];
            for (int i = 0; i < data.Length; i += 4)
            {
                int band = i / 4 % Bands;
                data[i] = toneBand >= 0 && Math.Abs(band - toneBand) <= 2 ? .6 : 0;
                data[i + 1] = attackBand >= 0 && Math.Abs(band - attackBand) <= 2 ? .8 : 0;
                data[i + 3] = Math.Max(data[i], data[i + 1]);
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

    [TestCase("Spectrum", 0.0), TestCase("Spectrum", 60.0)]
    [TestCase("Histogram", 0.0), TestCase("Histogram", 60.0), Category("GPU")]
    public async Task WaveformThroughGlobalDiagnosticsEventuallyTurnsBlack(string scene, double gainDb)
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
            { Samples = e.Samples.AsSpan(0, e.SampleCount * 2).ToArray() });
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
        Assert.That(rows[^1].GlobalEvidence.CurrentMean, Is.Zero);
        Assert.That(rows[^1].GlobalEvidence.Structure, Is.Zero);
        Assert.That(rows[^1].GlobalEvidence.InteractionRms, Is.Zero);

        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, Width, Height, options.Domain.Length,
            sampleRate / (double)batch, shaderDirectory: ShaderDirectory, timeProvider: clock);
        renderer.SelectScene(scene);
        var parameters = renderer.ActiveShader!.Parameters!;
        parameters.SetBoolean(parameters.Items.Single(p => p.Name == "ShowGlobalSTBR"), true);
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
                $"Production waveform/DFT/global fit: final frame {frame}.");
        }
        void Tick() { clock.Advance(); renderer.Render(false); }
    }

    [Test, Category("GPU")]
    public void GlobalShaderInputsRetainSignedLiveAndAlignedHistoricalEvidence()
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var shaders = Path.Combine(temporaryRoot, "AEther-global-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(shaders, "Common"));
        Directory.CreateDirectory(Path.Combine(shaders, "Scenes"));
        try
        {
            File.Copy(Path.Combine(ShaderDirectory, "Common", "Inputs.hlsl"), Path.Combine(shaders, "Common", "Inputs.hlsl"));
            File.Copy(Path.Combine(ShaderDirectory, "Common", "Present.hlsl"), Path.Combine(shaders, "Common", "Present.hlsl"));
            File.WriteAllText(Path.Combine(shaders, "Scenes", "GlobalProbe.hlsl"), """
                #include "../Common/Inputs.hlsl"
                float4 PS(PSin input) : SV_Target
                {
                    float age = .05;
                    float4 g = GlobalSTBRAtAge(age);
                    float4 local = Histogram0.SampleLevel(Linear,
                        float2(.5, HistogramShift - age / HistorySeconds * HistorySpan), 0);
                    float liveError = length(GlobalSTBR - float4(.1225, -.245, .245, .06125));
                    float historyError = abs(g.b - local.b) + abs(g.g + local.b)
                        + abs(g.r - .5 * local.b) + abs(g.a - .25 * local.b);
                    return float4(step(1e-5, liveError), step(1e-5, historyError),
                        step(g.b, 0), 1);
                }
                """);
            using var window = new HiddenWindow();
            var clock = new RenderClock();
            using var renderer = new Renderer(window.Handle, Width, Height, 1, 60,
                shaderDirectory: shaders, timeProvider: clock);
            for (int row = 0; row < 30; row++)
            {
                double b = .1 + .005 * row;
                renderer.PostAudio(new(new[] { 0.0, 0, b, 0 }, 4, DateTime.UnixEpoch)
                    { AnalysisRow = row, GlobalEvidence = new(b, -b, .5 * b, .25 * b, 1, 3) }, 1);
                clock.Advance(); renderer.Render(false);
            }
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero,
                "Red: incorrect live packet/layout; green: history misalignment or lost T sign; blue: history missing.");
        }
        finally
        {
            if (!Path.GetFullPath(shaders).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Global probe cleanup escaped the temporary directory.");
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

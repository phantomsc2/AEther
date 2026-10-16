using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows.Forms;
using AEther;
using AEther.Vortice;

internal static partial class Program
{
    static long nextSeedRow;
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--windows-only")) { CheckShaderWindows(null); return 0; }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
            var output = Path.Combine(root, "tools", "AEther.Smoke", "artifacts");
            Directory.CreateDirectory(output);
            var shaderDirectory = Path.Combine(output, "shader-reload");
            Directory.CreateDirectory(shaderDirectory);
            CopyShaderTree(Path.Combine(root, "AEther.Vortice", "Shaders"), shaderDirectory);
            using var form = new Form { ClientSize = new(640, 360), ShowInTaskbar = false };
            if (args.Contains("--shader-authoring-only") || !args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
            { CheckShaderAuthoring(form.Handle, shaderDirectory, output); CheckShaderWindows(shaderDirectory); return 0; }
            CheckColorOutput(form.Handle, shaderDirectory, output);
            if (args.Contains("--output-only")) return 0;
            CheckTransientSpectrum(form.Handle, shaderDirectory, output);
            if (args.Contains("--transient-only")) return 0;
            CheckVariations(form.Handle, shaderDirectory, output);
            if (args.Contains("--variation-only")) return 0;
            CheckFluid(form.Handle, shaderDirectory, output);
            if (args.Contains("--fluid-only")) return 0;
            CheckBilateral(form.Handle, shaderDirectory, output);
            CheckMidrange(form.Handle, shaderDirectory);
            if (args.Contains("--bilateral-only")) { CheckWindows(); return 0; }
            for (var restart = 0; restart < 2; restart++)
            {
                using var renderer = new Renderer(form.Handle, 640, 360, 64, 256, 1,
                    shaderDirectory);
                Seed(renderer);
                var hashes = new System.Collections.Generic.HashSet<string>();
                foreach (var scene in Enum.GetValues<Scene>())
                {
                    renderer.Scene = scene;
                    Seed(renderer);
                    renderer.Render(false);
                    var pixels = renderer.ReadPixels();
                    if (pixels.Where((_, i) => i % 4 != 3).Count(v => v > 10) < 100)
                        throw new InvalidOperationException($"{scene} rendered no visible audio.");
                    hashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                    Save(pixels, 640, 360, Path.Combine(output, $"vortice-{scene}-{restart}.png"));
                    renderer.Render();
                    Console.WriteLine($"Rendered {scene}, session {restart + 1}");
                }
                if (hashes.Count != Enum.GetValues<Scene>().Length) throw new InvalidOperationException("Scenes produced identical images.");
                renderer.Resize(320, 240);
                renderer.Render(false);
                if (renderer.ReadPixels().Length != 320 * 240 * 4) throw new Exception("Resize failed.");
                renderer.Resize(640, 360);
                if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
                var shaderPath = Path.Combine(shaderDirectory, "Mandelbox.hlsl");
                var workingShader = File.ReadAllText(shaderPath);
                File.WriteAllText(shaderPath, workingShader + "\nTHIS IS AN INVALID SHADER\n");
                if (renderer.ReloadShaders() || string.IsNullOrEmpty(renderer.ShaderError))
                    throw new Exception("Invalid shader was accepted.");
                renderer.Render(false); // Last working GPU shaders must still be usable.
                File.WriteAllText(shaderPath, workingShader);
                if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
                Seed(renderer);
                renderer.Scene = Scene.Histogram;
                renderer.Render(false);
                System.Threading.Thread.Sleep(1150);
                renderer.Render(false);
                var silent = renderer.ReadPixels();
                if (silent.Where((_, i) => i % 4 != 3).Any(v => v != 0))
                    throw new Exception("History failed to expire during silent capture.");
                var musicalTime = renderer.Audio.MusicalTime;
                var velocity = renderer.Audio.MusicalVelocity;
                renderer.Scene = Scene.Mandelbox;
                renderer.Render(false);
                if (renderer.Audio.MusicalTime < musicalTime || renderer.Audio.MusicalVelocity > velocity)
                    throw new Exception("Silent Mandelbox momentum failed to coast down.");
                if (renderer.ReadPixels().Where((_, i) => i % 4 != 3).Any(v => v != 0))
                    throw new Exception("Mandelbox hits failed to expire during silent capture.");
                // Resume consecutive sample rows after the packet-free pause.
                // Keep the same renderer and scene; recreating them hides clock bugs.
                Seed(renderer);
                renderer.Render(false);
                if (renderer.Audio.NewestAge > .03f)
                    throw new Exception("Resumed audio remained behind the presentation clock.");
                var resumed = renderer.ReadPixels();
                if (resumed.Where((_, i) => i % 4 != 3).Count(v => v > 10) < 10000)
                    throw new Exception("Mandelbox history stayed dark after resuming audio.");
                Save(resumed, 640, 360, Path.Combine(output, $"vortice-Mandelbox-resumed-{restart}.png"));
            }
            Console.WriteLine("Vortice: all scenes, resize, shader reload, silence expiry, pause/resume and session recreation passed.");
            CheckStereoCircle(form.Handle, shaderDirectory, output);
            CheckAntialiasing(form.Handle, shaderDirectory, output);
            CheckDepthAndAaAlignment(form.Handle, output);
            CheckHeadMapping(form.Handle, root, output);
            CheckWindows();
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void CheckBilateral(IntPtr window, string shaders, string output)
    {
        var directory = Path.Combine(output, "bilateral-check");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.GetFiles(shaders, "*.hlsl"))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        // Inspect the filtered texture directly, independent of fractal shading.
        File.WriteAllText(Path.Combine(directory, "Mandelbox.hlsl"), """
            float4 PS(PSDefaultin input) : SV_Target
            {
                uint w, h; Histogram0.GetDimensions(w, h);
                return Histogram0.Load(int3(int2(input.UV * float2(w, h)), 0));
            }
            """);
        // Use the same direct inspection for the analyzer toggle, avoiding
        // wall-clock history scrolling during byte-for-byte comparisons.
        File.Copy(Path.Combine(directory, "Mandelbox.hlsl"), Path.Combine(directory, "Histogram.hlsl"), true);
        using var renderer = new Renderer(window, 64, 32, 16, 64, 1, directory)
        { BilateralStrength = 0 };
        var data = new double[16 * 4];
        for (var band = 0; band < 16; band++)
        {
            var amplitude = (band < 8 ? .2 : .8) + .02 * (band % 2);
            data[band * 4] = data[band * 4 + 1] = data[band * 4 + 2] = amplitude;
            data[band * 4 + 3] = 1;
        }
        for (var row = 0; row < 64; row++)
            renderer.PostAudio(new(data, 64, DateTime.Now) { AnalysisRow = row }, 1);
        renderer.Render(false);
        var raw = renderer.ReadPixels();
        renderer.BilateralStrength = 1;
        renderer.Render(false);
        var filtered = renderer.ReadPixels();
        int At(int band) => (16 * 64 + band * 4 + 2) * 4;
        if (Math.Abs(filtered[At(5)] - filtered[At(6)]) >= Math.Abs(raw[At(5)] - raw[At(6)]))
            throw new Exception("Bilateral filter did not smooth small amplitude variations.");
        if (filtered[At(8)] - filtered[At(7)] < 140)
            throw new Exception("Bilateral filter blurred a strong amplitude edge.");
        if (!renderer.Audio.History[0].Contains(.2f))
            throw new Exception("Bilateral filter modified original history.");
        renderer.BilateralStrength = 0;
        renderer.Render(false);
        if (!raw.SequenceEqual(renderer.ReadPixels())) throw new Exception("Bilateral bypass changed the raw history.");
        renderer.Scene = Scene.Histogram;
        renderer.Render(false);
        var histogram = renderer.ReadPixels();
        renderer.BilateralStrength = 1;
        renderer.Render(false);
        if (!histogram.SequenceEqual(renderer.ReadPixels())) throw new Exception("Bilateral filtering affected the analyzer view.");
        renderer.FilteredHistogramView = true;
        renderer.Render(false);
        if (histogram.SequenceEqual(renderer.ReadPixels())) throw new Exception("Filtered histogram toggle did not change the view.");
        renderer.FilteredHistogramView = false;
        renderer.Render(false);
        if (!histogram.SequenceEqual(renderer.ReadPixels())) throw new Exception("Raw histogram toggle did not restore the view.");
        Console.WriteLine("Bilateral: small variations smoothed, strong edges preserved, raw history and analyzer views unchanged, bypass exact.");
    }

    static void CheckColorOutput(IntPtr window, string shaders, string output)
    {
        var fixture = Path.Combine(output, "output-check");
        Directory.CreateDirectory(fixture);
        foreach (var file in Directory.GetFiles(shaders, "*.hlsl"))
            File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
        File.WriteAllText(Path.Combine(fixture, "Spectrum.hlsl"), """
            float4 PS(PSDefaultin input) : SV_Target
            {
                float value = input.UV.y < .5 ? .002 + .078 * input.UV.x
                    : (input.UV.x < .2 ? 0 : (input.UV.x > .8 ? 1 : .041));
                return float4(value.xxx, 1);
            }
            """);
        const int width = 2048, height = 64;
        foreach (bool highPrecision in new[] { true, false })
        {
            using var renderer = new Renderer(window, width, height, 16, 64, 1, fixture, highPrecision)
            { Scene = Scene.Spectrum };
            renderer.Render(false);
            if (highPrecision && !renderer.HighPrecisionOutput)
                throw new Exception("The GPU did not expose FP16 scRGB for the precision check.");
            var presented = renderer.ReadPresentedPixels();
            if (presented.Any(v => !float.IsFinite(v) || v < 0)) throw new Exception("Invalid presentation pixels.");
            float At(int x, int y) => presented[(y * width + x) * 4];
            float white = At(width - 1, 48);
            if (At(0, 48) != 0 || white <= 0) throw new Exception("Output did not preserve black and white.");
            int levels = Enumerable.Range(0, width).Select(x => At(x, 16)).Distinct().Count();
            if (highPrecision)
            {
                if (levels < 1000) throw new Exception($"FP16 dark gradient retained only {levels} levels.");
                var encoded = (float)(Half).041f;
                var linear = Math.Pow((encoded + .055) / 1.055, 2.4);
                if (Math.Abs(At(width / 2, 48) / white - linear) > .000005)
                    throw new Exception("scRGB transfer function changed the scene brightness.");
                for (int x = 1; x < width; x++)
                    if (At(x, 16) < At(x - 1, 16)) throw new Exception("FP16 gradient lost monotonicity.");
            }
            else
            {
                if (levels > 23) throw new Exception("The forced 8-bit fallback was not quantized.");
                var flat = Enumerable.Range(512, 1024).Select(x => At(x, 48) * 255).ToArray();
                if (flat.Distinct().Count() != 2 || Math.Abs(flat.Average() - .041 * 255) > .04)
                    throw new Exception("8-bit output dither did not preserve the mean gradient color.");
            }
            var exported = renderer.ReadPixels();
            var undithered = renderer.ReadPixels(false);
            if (exported.SequenceEqual(undithered)) throw new Exception("8-bit export was not dithered.");
            renderer.Render(false);
            if (!presented.SequenceEqual(renderer.ReadPresentedPixels()))
                throw new Exception("Output dither flickered between identical frames.");
            Save(exported, width, height, Path.Combine(output, highPrecision ? "output-fp16-export.png" : "output-sdr8-export.png"));
            Console.WriteLine($"Output {renderer.OutputDescription}: {levels} dark gradient levels; transfer, black, dither and repeatability passed.");
            renderer.Resize(639, 359);
            renderer.Render(false);
            if (renderer.ReadPresentedPixels().Length != 639 * 359 * 4) throw new Exception("Output resize failed.");
            var presentPath = Path.Combine(fixture, "Present.hlsl");
            var source = File.ReadAllText(presentPath);
            try
            {
                File.WriteAllText(presentPath, source + "\nINVALID OUTPUT SHADER\n");
                if (renderer.ReloadShaders()) throw new Exception("Invalid output shader was accepted.");
                renderer.Render(false);
            }
            finally { File.WriteAllText(presentPath, source); }
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
        }
    }

    static void CheckFluid(IntPtr window, string shaders, string output)
    {
        var fixture = Path.Combine(output, "fluid-check");
        Directory.CreateDirectory(fixture);
        foreach (var file in Directory.GetFiles(shaders, "*.hlsl"))
            File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
        File.AppendAllText(Path.Combine(fixture, "Common.hlsl"), "\n#define MovementTime 0.0\n#define S 0.0\n#define FractalDepth 8.5\n");
        using var renderer = new Renderer(window, 640, 360, 64, 256, 1, fixture)
        { Scene = Scene.MandelFluid, MandelboxAntialiasing = false };
        renderer.Render(false);
        renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 10);
        if (renderer.Fluid.ReadDye().Any(v => v != 0)) throw new Exception("Fluid generated pigment in silence.");
        var projection = renderer.Fluid.CheckProjection(renderer.FluidPrograms);
        Console.WriteLine($"Fluid projection: RMS divergence {projection.Before:F6} -> {projection.After:F6}");
        if (projection.After > projection.Before * .08) throw new Exception("Multigrid projection did not remove divergence.");

        void Input(int component, bool stereo = false)
        {
            var data = new double[512];
            for (int c = stereo ? 1 : 0; c < 2; c++)
                for (int k = component == 2 ? 0 : 27; k < (component == 2 ? 64 : 37); k++)
                {
                    int i = c * 256 + k * 4;
                    data[i + component] = .65;
                    data[i + 3] = .65;
                }
            var first = nextSeedRow; nextSeedRow += 256;
            for (int row = 0; row < 256; row++)
                renderer.PostAudio(new(data, 256, DateTime.Now) { AnalysisRow = first + row }, 2);
            renderer.Render(false);
        }
        double Mass(float[] dye) => dye.Where((_, i) => i % 4 == 3).Sum(v => (double)v);
        double OuterMass(float[] dye)
        {
            double mass = 0;
            int w = renderer.Fluid.Width, h = renderer.Fluid.Height;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double px = (2 * (x + .5) / w - 1) * renderer.Fluid.Aspect;
                    double py = 2 * (y + .5) / h - 1;
                    if (px * px + py * py > .5 * .5) mass += dye[4 * (y * w + x) + 3];
                }
            return mass;
        }
        Input(0, true);
        renderer.Fluid.Clear();
        renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 90);
        var tonal = renderer.Fluid.ReadDye();
        var mass = Mass(tonal);
        if (tonal.Any(v => !float.IsFinite(v) || v < 0) || mass < 10)
            throw new Exception("Sustained tones failed to supply finite body material.");
        var velocity = renderer.Fluid.ReadVelocity();
        if (velocity.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1e-6))
            throw new Exception("Pure tones agitated the crystalline field without noise or attacks.");
        // Identical stationary input over one second must produce the same fluid
        // whether the display submits 30, 60 or 120 frames in that interval.
        float[] Scheduled(int frames, float diffusion = .001f)
        {
            renderer.Fluid.Clear();
            for (int i = 0; i < frames; i++)
                renderer.Fluid.Advance(1.0 / frames, renderer.Audio, renderer.FluidPrograms, renderer.ActiveShader!.Parameters);
            return renderer.Fluid.ReadDye();
        }
        var at60 = Scheduled(60);
        foreach (int frames in new[] { 30, 120 })
            if (Scheduled(frames).Zip(at60, (a, b) => Math.Abs(a - b)).Max() > .002)
                throw new Exception($"Fluid behavior changed at {frames} display frames per second.");
        if (!at60.SequenceEqual(Scheduled(60, .01f)))
            throw new Exception("The diffusion setting blurred pure tones despite zero noise.");
        // The scheduled runs can exceed the renderer's .5-second stall limit.
        // Refresh presentation timing, then seed a fresh release fixture so its
        // material is not cleared as stale when the screenshot is rendered.
        Input(0, true);
        renderer.Fluid.Clear();
        renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 60);
        renderer.Render(false);
        Save(renderer.ReadPixels(), 640, 360, Path.Combine(output, "mandelfluid-tone.png"));
        Console.WriteLine($"Crystalline tone: pigment mass {mass:F1}, zero turbulent velocity.");

        // No source remains: transport must persist while pigment dissipates.
        var releaseStartMass = Mass(renderer.Fluid.ReadDye());
        foreach (var history in renderer.Audio.History) Array.Clear(history);
        renderer.Render(false);
        renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 120);
        var released = renderer.Fluid.ReadDye();
        var releasedMass = Mass(released);
        Console.WriteLine($"Fluid release: mass {releaseStartMass:F1} -> {releasedMass:F1} after two seconds");
        if (releasedMass <= 0 || releasedMass >= releaseStartMass * .5)
            throw new Exception("Fluid did not linger and dissipate after sustained input ended.");
        renderer.Render(false);
        Save(renderer.ReadPixels(), 640, 360, Path.Combine(output, "mandelfluid-release.png"));

        Input(3); // Aggregate magnitude alone must keep the underlying field visible.
        renderer.Fluid.Clear();
        renderer.Render(false);
        var energyField = renderer.ReadPixels();
        if (energyField.Where((v, i) => i % 4 != 3 && v > 10).Count() < 1000)
            throw new Exception("Aggregate energy failed to reveal the persistent fractal field.");
        Save(energyField, 640, 360, Path.Combine(output, "mandelfluid-energy-field.png"));

        Input(2);
        renderer.Fluid.Clear();
        renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 90);
        var noise = renderer.Fluid.ReadDye();
        if (noise.Any(v => !float.IsFinite(v) || v < 0) || Mass(noise) < 10)
            throw new Exception("Noise failed to inject diffusing mist.");
        if (OuterMass(noise) < Mass(noise) * .25 || renderer.Fluid.ReadVelocity().All(v => Math.Abs(v) < .001))
            throw new Exception("Noise did not create global material and movement.");
        var unmixedNoise = Scheduled(60, 0);
        var mixedNoise = Scheduled(60, .01f);
        if (unmixedNoise.Zip(mixedNoise, (a, b) => Math.Abs(a - b)).Sum() < Mass(unmixedNoise) * .001)
            throw new Exception("Noise did not enable diffusion and mixing.");
        renderer.Render(false);
        Save(renderer.ReadPixels(), 640, 360, Path.Combine(output, "mandelfluid-noise.png"));

        Seed(renderer);
        renderer.Render(false);
        renderer.Fluid.Clear();
        renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 90);
        renderer.Render(false);
        Save(renderer.ReadPixels(), 640, 360, Path.Combine(output, "mandelfluid-mixed.png"));

        Input(0);
        renderer.Fluid.Clear();
        System.Threading.Thread.Sleep(180);
        renderer.Render(false);
        renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 60);
        if (Mass(renderer.Fluid.ReadDye()) != 0)
            throw new Exception("Silent capture kept injecting the last nonzero row.");

        // Transients are light, never pigment: clear any pre-existing body,
        // feed a recorded pulse, and inspect its center-to-edge history front.
        renderer.Fluid.Clear();
        Input(1);
        renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 20);
        if (Mass(renderer.Fluid.ReadDye()) != 0)
            throw new Exception("Transient-only input accumulated fluid material.");
        renderer.Render(false);
        Save(renderer.ReadPixels(), 640, 360, Path.Combine(output, "mandelfluid-shockwave.png"));
        var withAttack = renderer.ReadPixels();
        foreach (var history in renderer.Audio.History) Array.Clear(history);
        renderer.Render(false);
        var withoutAttack = renderer.ReadPixels();
        if (withAttack.Zip(withoutAttack, (a, b) => Math.Abs(a - b)).Average() < 2)
            throw new Exception("Recorded transient history did not highlight the field.");
        var excessive = new double[512];
        for (int i = 0; i < excessive.Length; i += 4)
        { excessive[i] = excessive[i + 1] = excessive[i + 2] = 1e6; excessive[i + 3] = 1; }
        for (int burst = 0; burst < 16; burst++)
        {
            for (int row = 0; row < 16; row++)
                renderer.PostAudio(new(excessive, 256, DateTime.Now) { AnalysisRow = nextSeedRow++ }, 2);
            renderer.Render(false);
            renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 3);
        }
        if (renderer.Fluid.ReadDye().Any(v => !float.IsFinite(v) || v < 0 || v > 12))
            throw new Exception("Repeated extreme input escaped the fluid's positive finite capacity.");
        if (renderer.Fluid.ReadVelocity().Any(v => !float.IsFinite(v) || Math.Abs(v) > 8))
            throw new Exception("Repeated extreme input destabilized the fluid velocity.");
        Console.WriteLine("Fluid stability: repeated million-amplitude tone/noise/attack bursts stayed finite and within pigment capacity.");
        foreach (var history in renderer.Audio.History) Array.Clear(history);
        renderer.Scene = Scene.Mandelbox;
        renderer.Render(false);
        renderer.Scene = Scene.MandelFluid;
        renderer.Render(false);
        if (Mass(renderer.Fluid.ReadDye()) != 0)
            throw new Exception("Scene re-entry retained stale pigment.");

        // Reload is transactional across both the scene and every solver pass.
        string simulationPath = Path.Combine(fixture, "FluidSimulation.hlsl");
        var working = File.ReadAllText(simulationPath);
        try
        {
            File.WriteAllText(simulationPath, working + "\nINVALID FLUID PROGRAM\n");
            if (renderer.ReloadShaders()) throw new Exception("Invalid fluid shader was accepted.");
            renderer.Render(false);
        }
        finally { File.WriteAllText(simulationPath, working); }
        if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
        foreach (var size in new[] { new Size(191, 97), new Size(97, 191), new Size(16, 16) })
        {
            renderer.Resize(size.Width, size.Height);
            Input(2);
            renderer.Fluid.AdvanceForCheck(renderer.FluidPrograms, 5);
            var resizedProjection = renderer.Fluid.CheckProjection(renderer.FluidPrograms);
            if (resizedProjection.After > resizedProjection.Before * .1)
                throw new Exception("Projection failed after aspect/size change.");
        }
        // Freeze presentation time and energy, place one pulse at two history
        // ages, and measure only the light added over the persistent field.
        string commonPath = Path.Combine(fixture, "Common.hlsl");
        var originalCommon = File.ReadAllText(commonPath);
        try
        {
            File.AppendAllText(commonPath, "\n#define HistogramShift .75\n#define HistoryAvailable 1.0\n#define HistoryNewestAge 0.0\n#define VisualBreath .5\n#define VisualFlash 0.0\n#define PoseX .5\n#define PoseY .5\n#define PoseZ .5\n");
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
            renderer.Resize(640, 360);
            foreach (var history in renderer.Audio.History) Array.Clear(history);
            renderer.Render(false);
            var unlit = renderer.ReadPixels(false);
            double FrontRadius(float age)
            {
                foreach (var history in renderer.Audio.History) Array.Clear(history);
                int row = (int)Math.Round((.75 - age * renderer.Audio.TimeScale) * renderer.Audio.Rows - .5);
                foreach (var history in renderer.Audio.History)
                    for (int y = row - 1; y <= row + 1; y++)
                        for (int band = 0; band < 64; band++)
                            history[(((y + renderer.Audio.Rows) % renderer.Audio.Rows) * 64 + band) * 4 + 1] = .8f;
                renderer.Fluid.Clear();
                renderer.Render(false);
                var lit = renderer.ReadPixels(false);
                double sum = 0, weighted = 0;
                for (int y = 0; y < 360; y++)
                    for (int x = 0; x < 640; x++)
                    {
                        int i = 4 * (y * 640 + x);
                        double added = Math.Max(0, lit[i] + lit[i + 1] + lit[i + 2] - unlit[i] - unlit[i + 1] - unlit[i + 2]);
                        double px = (2 * (x + .5) / 640 - 1) * (640.0 / 360), py = 2 * (y + .5) / 360 - 1;
                        sum += added; weighted += added * Math.Sqrt(px * px + py * py);
                    }
                if (sum < 1000) throw new Exception("History shockwave failed to illuminate the field.");
                Save(lit, 640, 360, Path.Combine(output, age < .1 ? "mandelfluid-front-early.png" : "mandelfluid-front-late.png"));
                return weighted / sum;
            }
            var early = FrontRadius(.06f);
            var late = FrontRadius(.32f);
            if (late <= early * 1.3) throw new Exception("Transient shockwaves did not travel from center to edge.");
            Console.WriteLine($"History shockwave: mean radius {early:F3} -> {late:F3}, no deposited pigment.");
        }
        finally
        {
            File.WriteAllText(commonPath, originalCommon);
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
        }
        Console.WriteLine("MandelFluid: energy field, crystalline tones, noise-driven mixing, history shockwaves, release, stability and lifecycle checks passed.");
    }

    static void CheckMidrange(IntPtr window, string shaders)
    {
        using var renderer = new Renderer(window, 320, 180, 64, 256, 1, shaders);
        var data = new double[64 * 4];
        for (var band = 29; band < 35; band++) data[band * 4] = data[band * 4 + 3] = .5;
        for (var row = 0; row < 256; row++)
            renderer.PostAudio(new(data, 256, DateTime.Now) { AnalysisRow = row }, 1);
        renderer.Render(false);
        var pixels = renderer.ReadPixels();
        long sides = 0, floor = 0;
        for (var y = 0; y < 180; y++)
            for (var x = 0; x < 320; x++)
            {
                var i = (y * 320 + x) * 4;
                var light = pixels[i] + pixels[i + 1] + pixels[i + 2];
                if (Math.Abs(y - 90) < 20 && Math.Abs(x - 160) > 40) sides += light;
                if (y > 130) floor += light;
            }
        if (sides < 1000 || floor > sides / 10)
            throw new Exception("Midrange tones did not stay at the sides of the Mandelbox.");
        Console.WriteLine("Midrange mapping: sustained center-frequency tones stay at the sides, away from the floor.");
    }

    static void CheckStereoCircle(IntPtr window, string shaders, string output)
    {
        for (var channel = 0; channel < 2; channel++)
            for (var treble = 0; treble < 2; treble++)
            {
                using var renderer = new Renderer(window, 640, 360, 64, 256, 1, shaders);
                var data = new double[512];
                for (var band = treble == 0 ? 0 : 48; band < (treble == 0 ? 16 : 64); band++)
                {
                    var i = channel * 256 + band * 4;
                    data[i] = data[i + 3] = .5;
                }
                for (var row = 0; row < 256; row++)
                    renderer.PostAudio(new(data, 256, DateTime.Now) { AnalysisRow = row }, 2);
                renderer.Render(false);
                var pixels = renderer.ReadPixels();
                long active = 0, silent = 0, top = 0, bottom = 0;
                for (var y = 0; y < 360; y++)
                    for (var x = 0; x < 640; x++)
                    {
                        var i = (y * 640 + x) * 4;
                        var light = pixels[i] + pixels[i + 1] + pixels[i + 2];
                        if ((x < 320 ? 0 : 1) != channel) { silent += light; continue; }
                        active += light;
                        // Near the vertical axis the angular frequency endpoints
                        // are anchored even while the sides blend into the fractal.
                        if (Math.Abs(x - 320) > 8) continue;
                        if (y < 80) top += light;
                        if (y >= 280) bottom += light;
                    }
                if (active < 1000 || silent != 0)
                    throw new Exception($"Stereo circle channel {channel} leaked or rendered no audio.");
                if (treble == 0 ? bottom <= top : top <= bottom)
                    throw new Exception("Stereo circle frequency orientation is reversed.");
                Save(pixels, 640, 360, Path.Combine(output, $"stereo-circle-{channel}-{treble}.png"));
            }
        Console.WriteLine("Stereo circle: isolated left/right channels, bass bottom and treble top passed.");
    }

    static void CheckAntialiasing(IntPtr window, string shaders, string output)
    {
        // Freeze only animation/history constants in a copy of the real shader.
        // A fixed synthetic history makes repeated renders byte-identical and
        // isolates sensitivity to a tiny slice movement at twenty iterations.
        var fixture = Path.Combine(output, "antialiasing-shaders");
        Directory.CreateDirectory(fixture);
        foreach (var file in Directory.GetFiles(shaders, "*.hlsl"))
            File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
        File.AppendAllText(Path.Combine(fixture, "Common.hlsl"), """

            #define S .37
            #define T .37
            #define FractalDepth 20.0
            #define HistoryAvailable 1.0
            #define HistoryNewestAge 0.0
            #define HistorySpan 1.0
            #define HistogramShift .5
            #define VisualFlash 0.0

            """);
        using var renderer = new Renderer(window, 320, 180, 64, 256, 1, fixture);
        FillContrastHistory(renderer);
        var movement = new double[2];
        var hashes = new System.Collections.Generic.HashSet<string>();
        foreach (var quality in new[] { false, true })
        {
            renderer.MandelboxAntialiasing = quality;
            foreach (var slice in new[] { 0f, .15f, .7f })
            {
                renderer.SliceDepth = slice;
                renderer.Render(false);
                var first = renderer.ReadPixels();
                renderer.Render(false);
                if (!first.SequenceEqual(renderer.ReadPixels()))
                    throw new Exception($"{quality} introduced noise into a stationary Mandelbox.");
                if (!first.Where((_, i) => i % 4 != 3).Any(v => v > 10)
                    || first.Where((_, i) => i % 4 == 3).Any(v => v != 255))
                    throw new Exception($"{quality} produced an empty or invalid Mandelbox.");
                renderer.SliceDepth += .0001f;
                renderer.Render(false);
                var shifted = renderer.ReadPixels();
                movement[quality ? 1 : 0] += first.Select((v, i) => i % 4 == 3 ? 0 : Math.Abs(v - shifted[i])).Average();
                if (slice == 0)
                {
                    hashes.Add(Convert.ToHexString(SHA256.HashData(first)));
                    Save(first, 320, 180, Path.Combine(output, $"mandelbox-aa-{quality}.png"));
                }
            }
            Console.WriteLine($"Mandelbox {quality}, depth 20: slice sensitivity {movement[quality ? 1 : 0]:F4}");
        }
        if (hashes.Count != 2) throw new Exception("Antialiasing toggle did not change the image.");
        if (movement[1] >= movement[0])
            throw new Exception("Antialiasing failed to reduce subpixel slice shimmer.");
        Console.WriteLine("Antialiasing: enabled and disabled, depth 20, deterministic frames and reduced slice shimmer passed.");
    }

    static void FillContrastHistory(Renderer renderer)
    {
        for (var channel = 0; channel < 2; channel++)
            for (var row = 0; row < renderer.Audio.Rows; row++)
                for (var band = 0; band < renderer.Audio.Bands; band++)
                {
                    var i = (row * renderer.Audio.Bands + band) * 4;
                    var signal = (float)(.02 + .3 * Math.Pow(Math.Sin(band * .57 + row * .19), 8));
                    renderer.Audio.History[channel][i] = signal;
                    renderer.Audio.History[channel][i + 1] = signal;
                    renderer.Audio.History[channel][i + 2] = .03f;
                }
    }

    static void CheckHeadMapping(IntPtr window, string root, string output)
    {
        var command = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-c", "safe.directory=" + root.Replace('\\', '/'), "-C", root, "show", "HEAD:AEther.Vortice/Shaders/Mandelbox.hlsl" })
            command.ArgumentList.Add(argument);
        using var git = System.Diagnostics.Process.Start(command)!;
        var head = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new Exception("Could not read HEAD shader for baseline comparison.");
        byte[] RenderFixture(string name, string shader)
        {
            // Compare geometry/history mapping independently of palette, tone
            // and transient shading edits. Noise light still exercises the field.
            shader = System.Text.RegularExpressions.Regex.Replace(shader,
                @"(?m)^\s*float3 rayPalette = .*?;", "    float3 rayPalette = float3(.6, .8, 1);");
            shader = System.Text.RegularExpressions.Regex.Replace(shader,
                @"(?m)^\s*float3 hullPalette = .*?;", "    float3 hullPalette = float3(1, .6, .3);");
            shader = System.Text.RegularExpressions.Regex.Replace(shader,
                @"(?m)^\s*float sustain = .*?;", "    float sustain = 0;");
            shader = System.Text.RegularExpressions.Regex.Replace(shader,
                @"(?m)^\s*float sustainedHull = .*?;", "    float sustainedHull = 0;");
            foreach (var component in new[] { "attack", "broad", "coreFlash" })
                shader = System.Text.RegularExpressions.Regex.Replace(shader,
                    @"(?m)^\s*float " + component + @" = .*?;", "    float " + component + " = 0;");
            var directory = Path.Combine(output, name);
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.GetFiles(Path.Combine(output, "antialiasing-shaders"), "*.hlsl"))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
            var commonPath = Path.Combine(directory, "Common.hlsl");
            // Compare the same origin pose; motion speed is deliberately
            // adjustable independently of HEAD's distance/history mapping.
            File.WriteAllText(commonPath, File.ReadAllText(commonPath)
                .Replace("#define FractalDepth 20.0", "#define FractalDepth 8.5")
                .Replace("#define S .37", "#define S 0.0"));
            File.WriteAllText(Path.Combine(directory, "Mandelbox.hlsl"), shader);
            using var renderer = new Renderer(window, 320, 180, 64, 256, 1, directory)
            { MandelboxAntialiasing = false,
                MandelboxJuliaBlend = 0, MandelboxFoldRotation = 0, MandelboxAnisotropy = 0 };
            FillContrastHistory(renderer);
            renderer.Render(false);
            var pixels = renderer.ReadPixels();
            Save((byte[])pixels.Clone(), 320, 180, Path.Combine(output, name + ".png"));
            return pixels;
        }
        var baseline = RenderFixture("mandelbox-head", head);
        var current = RenderFixture("mandelbox-restored", File.ReadAllText(Path.Combine(output, "antialiasing-shaders", "Mandelbox.hlsl")));
        var error = baseline.Select((v, i) => Math.Abs(v - current[i])).Average();
        if (error > .1) throw new Exception($"Restored mapping differs from HEAD: mean byte error {error}.");
        Console.WriteLine($"HEAD mapping comparison: mean byte error {error:F6}");
    }

    static double Linear(byte value) => Math.Pow(value / 255.0, 2.2);

    static void CheckTransientSpectrum(IntPtr window, string shaders, string output)
    {
        var fixture = Path.Combine(output, "transient-spectrum-shaders");
        Directory.CreateDirectory(fixture);
        foreach (var file in Directory.GetFiles(shaders, "*.hlsl"))
            File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
        File.AppendAllText(Path.Combine(fixture, "Common.hlsl"), """

            #define S 0.0
            #define MovementTime 0.0
            #define FractalDepth 8.5
            #define HistoryAvailable 1.0
            #define HistoryNewestAge 0.0
            #define HistorySpan 1.0
            #define HistogramShift .5
            #define VisualFlash 0.0

            """);
        using var renderer = new Renderer(window, 640, 360, 64, 256, 1, fixture)
        { MandelboxAntialiasing = true, BilateralStrength = 0 };
        foreach (var channel in new[] { 0, 1 })
        foreach (var target in new[] { .12, .5, .88 })
        {
            foreach (var history in renderer.Audio.History) Array.Clear(history);
            var historyData = renderer.Audio.History[channel];
            for (var row = 0; row < 256; row++)
            for (var band = 0; band < 64; band++)
            {
                var age = (.5 - (row + .5) / 256 + 1) % 1;
                var frequency = (band + .5) / 64;
                historyData[(row * 64 + band) * 4 + 1] =
                    (float)(.6 * Math.Exp(-Math.Pow((age - .13) / .018, 2))
                        * Math.Exp(-Math.Pow((frequency - target) / .055, 2)));
            }
            renderer.Render(false);
            var pixels = renderer.ReadPixels();
            renderer.Render(false);
            if (!pixels.SequenceEqual(renderer.ReadPixels())) throw new Exception("Transient spectrum pose drifted.");
            double near = 0, far = 0, leakage = 0;
            for (var y = 0; y < 360; y++)
            for (var x = 0; x < 640; x++)
            {
                var index = (y * 640 + x) * 4;
                var light = pixels[index] + pixels[index + 1] + pixels[index + 2];
                if ((x < 320 ? 0 : 1) != channel) { leakage += light; continue; }
                var frequency = Math.Atan2(Math.Abs((2 * (x + .5) / 640 - 1) * (640.0 / 360)),
                    2 * (y + .5) / 360 - 1) / Math.PI;
                if (Math.Abs(frequency - target) < .2) near += light;
                else if (Math.Abs(frequency - target) > .3) far += light;
            }
            // Bass and treble deliberately share both channels at the poles;
            // the middle of each semicircle retains strong stereo separation.
            var stereoFailed = target == .5 ? leakage > near / 3 : leakage <= 0;
            if (near < 10000 || far > near * .05 || stereoFailed)
                throw new Exception($"Transient frequency {target}, channel {channel}: near {near}, far {far}, leakage {leakage}.");
            var label = target < .3 ? "bass" : target > .7 ? "treble" : "mids";
            Save(pixels, 640, 360, Path.Combine(output, $"transient-spectrum-{channel}-{label}.png"));
        }
        Console.WriteLine("Transient spectrum: bass/mids/treble fronts, mono poles, stereo mids and frozen poses passed.");
    }

    static void CheckVariations(IntPtr window, string shaders, string output)
    {
        var fixture = Path.Combine(output, "variation-shaders");
        Directory.CreateDirectory(fixture);
        foreach (var file in Directory.GetFiles(shaders, "*.hlsl"))
            File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)), true);
        var commonPath = Path.Combine(fixture, "Common.hlsl");
        var common = File.ReadAllText(commonPath) + """

            #define S 0.0
            #define FractalDepth 8.5
            #define HistoryAvailable 1.0
            #define HistoryNewestAge 0.0
            #define HistorySpan 1.0
            #define HistogramShift .5
            #define VisualFlash 0.0

            """;
        using var renderer = new Renderer(window, 640, 360, 64, 256, 1, fixture)
        { MandelboxAntialiasing = true };
        FillContrastHistory(renderer);
        byte[] Capture(string name, float julia, float rotation, float anisotropy, int time)
        {
            File.WriteAllText(commonPath, common + $"\n#define MovementTime {time}.0\n");
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
            renderer.MandelboxJuliaBlend = julia;
            renderer.MandelboxFoldRotation = rotation;
            renderer.MandelboxAnisotropy = anisotropy;
            renderer.Render(false);
            var image = renderer.ReadPixels();
            renderer.Render(false);
            if (!image.SequenceEqual(renderer.ReadPixels())) throw new Exception("Frozen variation pose drifted.");
            if (image.Where((_, i) => i % 4 != 3).Count(v => v > 10) < 10000)
                throw new Exception("Variation lost visible structure.");
            Save(image, 640, 360, Path.Combine(output, $"variation-{name}.png"));
            return image;
        }
        var baseline = Capture("original", 0, 0, 0, 0);
        foreach (var setting in new[] { ("julia", .3f, 0f, 0f), ("rotation", 0f, .35f, 0f),
            ("anisotropy", 0f, 0f, .22f), ("combined", .3f, .35f, .22f) })
        {
            var image = Capture(setting.Item1, setting.Item2, setting.Item3, setting.Item4, 0);
            var difference = image.Select((v, i) => Math.Abs(v - baseline[i])).Average();
            if (difference < 1) throw new Exception($"{setting.Item1} failed to change the field.");
            Console.WriteLine($"Variation {setting.Item1}: mean byte change {difference:F2}");
        }
        var poses = new System.Collections.Generic.HashSet<string>();
        foreach (var time in new[] { 0, 4, 12, 30, 70 })
            poses.Add(Convert.ToHexString(SHA256.HashData(Capture($"time-{time}", .3f, .35f, .22f, time))));
        if (poses.Count != 5) throw new Exception("Musical time failed to explore distinct variation poses.");
        Capture("maximum", 1, 1, .5f, 30);
        Console.WriteLine("Variations: independent controls, musical poses, maximum settings and frozen-frame determinism passed.");
    }

    static void CheckDepthAndAaAlignment(IntPtr window, string output)
    {
        var shaders = Path.Combine(output, "antialiasing-shaders");
        var commonPath = Path.Combine(shaders, "Common.hlsl");
        var common = File.ReadAllText(commonPath);
        using var renderer = new Renderer(window, 96, 54, 64, 256, 1, shaders);
        FillContrastHistory(renderer);
        byte[] AtDepth(string depth)
        {
            File.WriteAllText(commonPath, common.Replace("#define FractalDepth 20.0", "#define FractalDepth " + depth));
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
            renderer.Render(false);
            return renderer.ReadPixels();
        }
        try
        {
            foreach (var quality in new[] { false, true })
            {
                renderer.MandelboxAntialiasing = quality;
                var lower = AtDepth("8.0");
                var middle = AtDepth("8.5");
                foreach (var near in new[] { AtDepth("7.999"), AtDepth("8.001") })
                {
                    var differences = near.Select((v, i) => Math.Abs(v - lower[i])).ToArray();
                    if (differences.Max() > 4 || differences.Average() > .02)
                        throw new Exception($"{quality} popped at an integer depth: max {differences.Max()}, mean {differences.Average()}.");
                }
                Save(middle, 96, 54, Path.Combine(output, $"mandelbox-depth-travel-{quality}.png"));
            }
            // Supersampling must equal a larger render of the same field.
            renderer.MandelboxAntialiasing = true;
            foreach (var depth in new[] { "1.0", "128.0", "4096.0", "2147483647.0" })
            {
                var pixels = AtDepth(depth);
                renderer.Render(false);
                if (!pixels.SequenceEqual(renderer.ReadPixels())
                    || !pixels.Where((_, i) => i % 4 != 3).Any(v => v > 10)
                    || pixels.Where((_, i) => i % 4 == 3).Any(v => v != 255))
                    throw new Exception($"Uncapped depth {depth} produced empty or unstable output.");
            }
            Console.WriteLine("Iteration setting: depth 1, 128, 4096 and int.MaxValue rendered stable output.");
            // Supersampling must equal a larger render of the same field.
            var scaleHashes = new System.Collections.Generic.HashSet<string>();
            foreach (var scale in new[] { -3f, -2f, -1.5f, -.5f, 0f, .5f, 1.5f, 2f })
            {
                renderer.MandelboxScale = scale;
                var pixels = AtDepth("12.0");
                renderer.Render(false);
                if (!pixels.SequenceEqual(renderer.ReadPixels()))
                    throw new Exception($"Scale {scale} produced unstable output.");
                scaleHashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                Save(pixels, 96, 54, Path.Combine(output, $"mandelbox-scale-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));
            }
            if (scaleHashes.Count < 6) throw new Exception("Scale changes did not reshape the fractal.");
            renderer.MandelboxScale = -1.5f;
            Console.WriteLine("Mandelbox scale: negative, zero and positive values produced stable, distinct renders.");
            // Exercise fractional depth so this also checks operation ordering.
            // HEAD contains quad-derivative shading heuristics. Disable those
            // in this fixture to isolate spatial integration of the same field;
            // finite differences across different quad grids need not agree.
            File.WriteAllText(commonPath, common.Replace("#define FractalDepth 20.0", "#define FractalDepth 8.5")
                + "\n#define fwidth(value) 0.0\n");
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
            using var referenceWindow = new Form { ShowInTaskbar = false };
            foreach (var samples in new[] { 2 })
            {
                renderer.MandelboxAntialiasing = true;
                renderer.Render(false);
                var actual = renderer.ReadPixels();
                using var reference = new Renderer(referenceWindow.Handle, 96 * samples, 54 * samples, 64, 256, 1, shaders)
                { MandelboxAntialiasing = false };
                FillContrastHistory(reference);
                reference.Render(false);
                var large = reference.ReadPixels();
                double totalError = 0;
                for (var y = 0; y < 54; y++)
                    for (var x = 0; x < 96; x++)
                        for (var c = 0; c < 3; c++)
                        {
                            double average = 0;
                            for (var sy = 0; sy < samples; sy++)
                                for (var sx = 0; sx < samples; sx++)
                                    average += Linear(large[((y * samples + sy) * 96 * samples + x * samples + sx) * 4 + c]);
                            average /= samples * samples;
                            totalError += Math.Abs(Linear(actual[(y * 96 + x) * 4 + c]) - average);
                        }
                var error = totalError / (96 * 54 * 3);
                if (error > .003) throw new Exception($"{samples}x AA changed the field: mean linear error {error}.");
                Console.WriteLine($"AA {samples}x matches averaged None render: mean linear error {error:F6}");
            }
        }
        finally { File.WriteAllText(commonPath, common); }
        Console.WriteLine("HEAD depth travel: integer continuity and spatial-AA alignment passed.");
    }

    static void Seed(Renderer renderer)
    {
        var bands = renderer.Audio.Bands;
        var end = DateTime.Now;
        var firstRow = nextSeedRow;
        nextSeedRow += 256;
        for (var row = 0; row < 256; row++)
        {
            var data = new double[bands * 8];
            for (var c = 0; c < 2; c++)
                for (var k = 0; k < bands; k++)
                {
                    var i = c * bands * 4 + k * 4;
                    data[i] = .15 + .55 * Math.Pow(Math.Sin(k * .12 + c * 2), 8);
                    data[i + 1] = row % 32 < 3 ? .8 : 0;
                    data[i + 2] = .08;
                    data[i + 3] = .65;
                }
            renderer.PostAudio(new(data, bands * 4, end - TimeSpan.FromSeconds((256 - row) / 256.0))
                { AnalysisRow = firstRow + row }, 2);
        }
    }

    static void Save(byte[] rgba, int width, int height, string path)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (var i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
        var map = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(rgba, 0, map.Scan0, rgba.Length); }
        finally { bitmap.UnlockBits(map); }
        bitmap.Save(path, ImageFormat.Png);
    }

    static void CheckWindows() => CheckShaderWindows(null);

    sealed class SyntheticSource(Action started) : SampleSource
    {
        public override SampleFormat Format { get; } = new(SampleType.Float32.Instance, 48000, 2);
        public override void Start()
        {
            started();
            DataAvailable?.Invoke(this, new byte[480 * 8]);
        }
        public override void Stop() => Stopped?.Invoke(this, null);
        public override void Dispose() { }
    }
}

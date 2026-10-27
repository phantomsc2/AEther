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
using Splitter = AEther.Splitter;

internal static partial class Program
{
    static (ShaderParameters Values, ShaderParameter Parameter) FindParameter(Renderer renderer, string path)
    {
        var values = renderer.ActiveShader?.Parameters
            ?? throw new InvalidOperationException($"{renderer.SceneName} has no parameters.");
        var parameter = ShaderParameters.Leaves(values.Items).Single(p => p.Path == path);
        return (values, parameter);
    }

    static void SetParameter(Renderer renderer, string path, float value)
    {
        var (values, parameter) = FindParameter(renderer, path);
        values.SetFloat(parameter, Math.Clamp(value, 0, 1));
    }

    static void SetParameter(Renderer renderer, string path, bool value)
    {
        var (values, parameter) = FindParameter(renderer, path);
        values.SetBoolean(parameter, value);
    }

    static float GetParameter(Renderer renderer, string path)
    {
        var (values, parameter) = FindParameter(renderer, path);
        return values.GetFloat(parameter);
    }

    static long nextSeedRow;
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--presentation-profile")) { ProfilePresentation(args); return 0; }
            if (args.Contains("--slider-probe")) { CheckSliderPointerEdits(); return 0; }
            if (args.Contains("--swift-settings-only")) { CheckSwiftSettings(); return 0; }
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
            if (args.Contains("--windows-watcher-only")) { CheckShaderWindows(shaderDirectory); return 0; }
            using var form = new Form { ClientSize = new(640, 360), ShowInTaskbar = false };
            if (args.Contains("--shader-authoring-only") || !args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
            { CheckShaderAuthoring(form.Handle, shaderDirectory, output); CheckShaderWindows(shaderDirectory); return 0; }
            if (args.Contains("--fluid-only")) { CheckFluid(form.Handle, shaderDirectory, output); return 0; }
            CheckColorOutput(form.Handle, shaderDirectory, output);
            if (args.Contains("--output-only")) return 0;
            CheckTransientSpectrum(form.Handle, shaderDirectory, output);
            if (args.Contains("--transient-only")) return 0;
            CheckVariations(form.Handle, shaderDirectory, output);
            if (args.Contains("--variation-only")) return 0;
            CheckFluid(form.Handle, shaderDirectory, output);
            CheckBilateral(form.Handle, shaderDirectory, output);
            CheckMidrange(form.Handle, shaderDirectory);
            if (args.Contains("--bilateral-only")) { CheckWindows(); return 0; }
            for (var restart = 0; restart < 2; restart++)
            {
                using var renderer = new Renderer(form.Handle, 640, 360, 64, 256, 1,
                    shaderDirectory);
                Seed(renderer);
                var hashes = new System.Collections.Generic.HashSet<string>();
                foreach (var scene in new[] { "Spectrum", "Histogram", "Mandelbox", "MandelFluid" })
                {
                    renderer.SelectScene(scene);
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
                if (hashes.Count != 4) throw new InvalidOperationException("Scenes produced identical images.");
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
                renderer.SelectScene("Histogram");
                renderer.Render(false);
                System.Threading.Thread.Sleep(1150);
                renderer.Render(false);
                var silent = renderer.ReadPixels();
                if (silent.Where((_, i) => i % 4 != 3).Any(v => v != 0))
                    throw new Exception("History failed to expire during silent capture.");
                var musicalTime = renderer.Audio.MusicalTime;
                var velocity = renderer.Audio.MusicalVelocity;
                renderer.SelectScene("Mandelbox");
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
        var domain = new Domain(100, 8, 16);
        var splitter = new Splitter(domain, new AnalyzerOptions());
        var input = Enumerable.Repeat(Math.Log10(.4), domain.Length).ToArray();
        var data = new double[domain.Length * Splitter.OutputStride];
        splitter.Process(input, data);
        if (Enumerable.Range(0, domain.Length).Any(k =>
            Math.Abs(data[4 * k]) > 1e-12 || Math.Abs(data[4 * k + 1]) > 1e-12 ||
            Math.Abs(data[4 * k + 2] - .4) > 1e-12 || Math.Abs(data[4 * k + 3] - .4) > 1e-12))
            throw new Exception("Gaussian STBR fit did not assign a constant field to background B.");
        Console.WriteLine("STBR: constant field belongs to background B; raw amplitude is preserved.");
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
            using var renderer = new Renderer(window, width, height, 16, 64, 1, fixture, highPrecision);
            renderer.SelectScene("Spectrum");
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
        string? reference = null;
        foreach (var scene in new[] { "MandelFluidv4", "MandelFluid", "MandelFluidv2", "MandelFluidv3" })
        {
            var clock = new FluidSmokeClock();
            using var renderer = new Renderer(window, 1280, 720, 64, 60,
                shaderDirectory: shaders, timeProvider: clock, initialScene: scene);
            if (renderer.ShaderError != null) throw new Exception(renderer.ShaderError);
            renderer.Render(false);
            if (renderer.Fluid.ReadDye().Any(v => v != 0)) throw new Exception("Fluid emitted in silence.");
            var projection = renderer.Fluid.CheckProjection(renderer.FluidPrograms);
            if (projection.After > .01 * projection.Before || projection.CurlError > 1e-4)
                throw new Exception("Pressure projection changed divergence or circulation.");
            for (int frame = 1; frame <= 60; frame++)
            {
                var samples = Enumerable.Range(0, 64 * 8).Select(i => .05 + .02 * (i % 7)).ToArray();
                renderer.PostAudio(new(samples, 256, DateTime.UnixEpoch) { AnalysisRow = frame }, 2);
                clock.Frame = frame; renderer.Render(false);
            }
            var dye = renderer.Fluid.ReadDye();
            if (dye.Any(v => !float.IsFinite(v) || v < 0) || dye.Max() < .01)
                throw new Exception("Live input failed to emit finite material.");
            string Hash(float[] values) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));
            var state = Hash(dye) + Hash(renderer.Fluid.ReadAge()) + Hash(renderer.Fluid.ReadVelocity());
            if (reference != null && state != reference) throw new Exception($"{scene} differs from the v4 simulation.");
            reference = state;
            Save(renderer.ReadPixels(false), 1280, 720, Path.Combine(output, scene.ToLowerInvariant() + "-shared-fluid.png"));
            foreach (var size in new[] { new Size(191, 97), new Size(97, 191), new Size(16, 16) })
            {
                renderer.Resize(size.Width, size.Height);
                renderer.Render(false);
                if (renderer.Fluid.Width != 512 || renderer.Fluid.Height != 512)
                    throw new Exception("Shared fluid grid changed with viewport size.");
            }
            Console.WriteLine($"{scene}: shared v4 material/motion, projection, silence and resize checks passed.");
        }
    }

    sealed class FluidSmokeClock : TimeProvider
    {
        public long Frame;
        public override long TimestampFrequency => 60;
        public override long GetTimestamp() => Frame;
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
            SetParameter(renderer, "Quality.Antialiasing", quality);
            foreach (var slice in new[] { 0f, .15f, .7f })
            {
                SetParameter(renderer, "Geometry.SliceDepth", (slice + 2) / 4);
                renderer.Render(false);
                var first = renderer.ReadPixels();
                renderer.Render(false);
                if (!first.SequenceEqual(renderer.ReadPixels()))
                    throw new Exception($"{quality} introduced noise into a stationary Mandelbox.");
                if (!first.Where((_, i) => i % 4 != 3).Any(v => v > 10)
                    || first.Where((_, i) => i % 4 == 3).Any(v => v != 255))
                    throw new Exception($"{quality} produced an empty or invalid Mandelbox.");
                SetParameter(renderer, "Geometry.SliceDepth", GetParameter(renderer, "Geometry.SliceDepth") + .0001f / 4);
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
        renderer.Audio.InvalidateHistory();
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
            using var renderer = new Renderer(window, 320, 180, 64, 256, 1, directory);
            SetParameter(renderer, "Quality.Antialiasing", false);
            SetParameter(renderer, "Geometry.JuliaBlend", 0);
            SetParameter(renderer, "Geometry.FoldRotation", 0);
            SetParameter(renderer, "Geometry.Anisotropy", 0);
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
        using var renderer = new Renderer(window, 640, 360, 64, 256, 1, fixture);
        SetParameter(renderer, "Quality.Antialiasing", true);
        foreach (var channel in new[] { 0, 1 })
        foreach (var target in new[] { .12, .5, .88 })
        {
            foreach (var history in renderer.Audio.History) Array.Clear(history);
            renderer.Audio.InvalidateHistory();
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
        using var renderer = new Renderer(window, 640, 360, 64, 256, 1, fixture);
        SetParameter(renderer, "Quality.Antialiasing", true);
        FillContrastHistory(renderer);
        byte[] Capture(string name, float julia, float rotation, float anisotropy, int time)
        {
            File.WriteAllText(commonPath, common + $"\n#define MovementTime {time}.0\n");
            if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
            SetParameter(renderer, "Geometry.JuliaBlend", julia);
            SetParameter(renderer, "Geometry.FoldRotation", rotation);
            SetParameter(renderer, "Geometry.Anisotropy", anisotropy * 2);
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
                SetParameter(renderer, "Quality.Antialiasing", quality);
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
            SetParameter(renderer, "Quality.Antialiasing", true);
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
                SetParameter(renderer, "Geometry.Scale", (scale + 4) / 8);
                var pixels = AtDepth("12.0");
                renderer.Render(false);
                if (!pixels.SequenceEqual(renderer.ReadPixels()))
                    throw new Exception($"Scale {scale} produced unstable output.");
                scaleHashes.Add(Convert.ToHexString(SHA256.HashData(pixels)));
                Save(pixels, 96, 54, Path.Combine(output, $"mandelbox-scale-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}.png"));
            }
            if (scaleHashes.Count < 6) throw new Exception("Scale changes did not reshape the fractal.");
            SetParameter(renderer, "Geometry.Scale", (-1.5f + 4) / 8);
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
                SetParameter(renderer, "Quality.Antialiasing", true);
                renderer.Render(false);
                var actual = renderer.ReadPixels();
                using var reference = new Renderer(referenceWindow.Handle, 96 * samples, 54 * samples, 64, 256, 1, shaders);
                SetParameter(reference, "Quality.Antialiasing", false);
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

    static void Seed(Renderer renderer, long generation = 0)
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
                  { AnalysisRow = firstRow + row, DiscontinuityGeneration = generation }, 2);
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

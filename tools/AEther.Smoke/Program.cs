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

internal static class Program
{
    static long nextSeedRow;
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
            var output = Path.Combine(root, "tools", "AEther.Smoke", "artifacts");
            Directory.CreateDirectory(output);
            var shaderDirectory = Path.Combine(output, "shader-reload");
            Directory.CreateDirectory(shaderDirectory);
            foreach (var file in Directory.GetFiles(Path.Combine(root, "AEther.Vortice", "Shaders"), "*.hlsl"))
                File.Copy(file, Path.Combine(shaderDirectory, Path.GetFileName(file)), true);
            using var form = new Form { ClientSize = new(640, 360), ShowInTaskbar = false };
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
                if (hashes.Count != 3) throw new InvalidOperationException("Scenes produced identical images.");
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
        var movement = new double[3];
        var hashes = new System.Collections.Generic.HashSet<string>();
        foreach (var quality in Enum.GetValues<AntialiasingQuality>())
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
                movement[(int)quality] += first.Select((v, i) => i % 4 == 3 ? 0 : Math.Abs(v - shifted[i])).Average();
                if (slice == 0)
                {
                    hashes.Add(Convert.ToHexString(SHA256.HashData(first)));
                    Save(first, 320, 180, Path.Combine(output, $"mandelbox-aa-{quality}.png"));
                }
            }
            Console.WriteLine($"Mandelbox {quality}, depth 20: slice sensitivity {movement[(int)quality]:F4}");
        }
        if (hashes.Count != 3) throw new Exception("Antialiasing quality settings did not change the image.");
        if (movement[1] >= movement[0] || movement[2] >= movement[0])
            throw new Exception("Antialiasing failed to reduce subpixel slice shimmer.");
        Console.WriteLine("Antialiasing: all quality modes, depth 20, deterministic frames and reduced slice shimmer passed.");
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
        foreach (var argument in new[] { "-c", "safe.directory=" + root, "-C", root, "show", "HEAD:AEther.Vortice/Shaders/Mandelbox.hlsl" })
            command.ArgumentList.Add(argument);
        using var git = System.Diagnostics.Process.Start(command)!;
        var head = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0) throw new Exception("Could not read HEAD shader for baseline comparison.");
        byte[] RenderFixture(string name, string shader)
        {
            // Compare geometry/history mapping independently of palette edits.
            shader = System.Text.RegularExpressions.Regex.Replace(shader,
                @"(?m)^\s*float3 rayPalette = .*?;", "    float3 rayPalette = float3(.6, .8, 1);");
            shader = System.Text.RegularExpressions.Regex.Replace(shader,
                @"(?m)^\s*float3 hullPalette = .*?;", "    float3 hullPalette = float3(1, .6, .3);");
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
            { MandelboxAntialiasing = AntialiasingQuality.None };
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
            foreach (var quality in Enum.GetValues<AntialiasingQuality>())
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
            renderer.MandelboxAntialiasing = AntialiasingQuality.High;
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
            foreach (var samples in new[] { 2, 4 })
            {
                renderer.MandelboxAntialiasing = samples == 2 ? AntialiasingQuality.Medium : AntialiasingQuality.High;
                renderer.Render(false);
                var actual = renderer.ReadPixels();
                using var reference = new Renderer(referenceWindow.Handle, 96 * samples, 54 * samples, 64, 256, 1, shaders)
                { MandelboxAntialiasing = AntialiasingQuality.None };
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
        var end = DateTime.Now;
        var firstRow = nextSeedRow;
        nextSeedRow += 256;
        for (var row = 0; row < 256; row++)
        {
            var data = new double[512];
            for (var c = 0; c < 2; c++)
                for (var k = 0; k < 64; k++)
                {
                    var i = c * 256 + k * 4;
                    data[i] = .15 + .55 * Math.Pow(Math.Sin(k * .12 + c * 2), 8);
                    data[i + 1] = row % 32 < 3 ? .8 : 0;
                    data[i + 2] = .08;
                    data[i + 3] = .65;
                }
            renderer.PostAudio(new(data, 256, end - TimeSpan.FromSeconds((256 - row) / 256.0))
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

    static void CheckWindows()
    {
        var started = 0;
        using var host = new AEther.WindowsForms.MainForm(() => new SyntheticSource(() => started++))
        { Opacity = 0, ShowInTaskbar = false };
        var settings = host.OwnedForms.OfType<AEther.WindowsForms.SettingsForm>().Single();
        settings.Opacity = 0;
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        var stage = 0;
        Exception? failure = null;
        var bounds = Rectangle.Empty;
        var startedBeforeRestart = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        void Key(Form form, Keys key)
        {
            var method = form.GetType().GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            method.Invoke(form, new object[] { default(Message), key });
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Window lifecycle check timed out.");
                switch (stage)
                {
                    case 0:
                        if (started == 0) return;
                        Key(host, Keys.Space);
                        if (!settings.Visible || settings.Owner != host) throw new Exception("Settings ownership/show failed.");
                        settings.Close();
                        if (settings.IsDisposed || settings.Visible || host.IsDisposed) throw new Exception("Closing settings exited the app.");
                        Key(host, Keys.Space);
                        var controls = settings.Controls.OfType<TableLayoutPanel>().Single().Controls;
                        var grid = controls.OfType<PropertyGrid>().Single(c => c.Name == "MandelboxProperties");
                         if (!ReferenceEquals(grid.SelectedObject, settings.Mandelbox) || settings.Mandelbox.MovementSpeed != 3) throw new Exception("Mandelbox grid/default speed is incorrect.");
                        var sessionField = typeof(AEther.WindowsForms.MainForm).GetField("session",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                        var active = (AEther.WindowsForms.Session)sessionField.GetValue(host)!;
                        foreach (var mode in Enum.GetValues<AntialiasingQuality>())
                        {
                            settings.Mandelbox.Antialiasing = mode;
                            if (active.Renderer.MandelboxAntialiasing != mode)
                                throw new Exception("Quality selection did not reach the renderer.");
                        }
                        settings.Mandelbox.MaximumIterations = 128;
                        settings.Mandelbox.MovementSpeed = .5f;
                         settings.Mandelbox.Scale = -2.1f;
                         settings.Mandelbox.BilateralStrength = .75f;
                         settings.Mandelbox.BilateralFrequencySigma = 1.5f;
                         settings.Mandelbox.BilateralTimeSigma = .5f;
                         settings.Mandelbox.BilateralRangeSigma = .2f;
                         settings.Mandelbox.FilteredHistogramView = true;
                        if (active.Renderer.MandelboxIterations != 128 || active.Renderer.MandelboxMovementSpeed != .5f
                             || active.Renderer.MandelboxScale != -2.1f || started != 1
                             || active.Renderer.BilateralStrength != .75f || active.Renderer.BilateralFrequencySigma != 1.5f
                             || active.Renderer.BilateralTimeSigma != .5f || active.Renderer.BilateralRangeSigma != .2f
                             || !active.Renderer.FilteredHistogramView
                            || !ReferenceEquals(active, sessionField.GetValue(host)))
                            throw new Exception("Rendering settings restarted capture or did not apply live.");
                        bounds = host.Bounds;
                        Key(settings, Keys.F11);
                        if (host.FormBorderStyle != FormBorderStyle.None) throw new Exception("Fullscreen entry failed.");
                        Key(settings, Keys.F11);
                        if (host.FormBorderStyle != FormBorderStyle.Sizable || host.Bounds != bounds) throw new Exception("Fullscreen restore failed.");
                        startedBeforeRestart = started;
                        var scenes = settings.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ComboBox>()
                            .Single(c => c.Name == "Scene");
                        scenes.SelectedItem = Scene.Histogram;
                        stage++;
                        break;
                    case 1:
                        if (started <= startedBeforeRestart) return;
                        if (!host.Text.Contains("Histogram")) throw new Exception("Scene restart failed.");
                        var currentField = typeof(AEther.WindowsForms.MainForm).GetField("session",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                        var current = (AEther.WindowsForms.Session)currentField.GetValue(host)!;
                        if (current.Renderer.MandelboxIterations != 128 || current.Renderer.MandelboxMovementSpeed != .5f
                             || current.Renderer.MandelboxScale != -2.1f
                             || current.Renderer.BilateralStrength != .75f || current.Renderer.BilateralFrequencySigma != 1.5f
                             || current.Renderer.BilateralTimeSigma != .5f || current.Renderer.BilateralRangeSigma != .2f
                             || !current.Renderer.FilteredHistogramView
                            || current.Renderer.MandelboxAntialiasing != AntialiasingQuality.High)
                            throw new Exception("Rendering settings were lost on session restart.");
                        Key(settings, Keys.Space);
                        if (settings.Visible) throw new Exception("Space did not hide settings.");
                        host.Close(); stage++;
                        break;
                }
            }
            catch (Exception error) { failure = error; timer.Stop(); host.Close(); }
        };
        timer.Start();
        Application.Run(host);
        if (failure != null) throw failure;
        if (stage != 2) throw new Exception("Window closed before lifecycle check finished.");
        Console.WriteLine("Window lifecycle: settings ownership, close/reopen, Space, F11, full session restart and shutdown passed.");
    }

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

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
            if (args.Contains("--presentation-profile")) { ProfilePresentation(args); return 0; }
            if (args.Contains("--slider-probe")) { CheckSliderPointerEdits(); return 0; }
            if (args.Contains("--swift-settings-only")) { CheckSwiftSettings(); return 0; }
            if (args.Contains("--windows-only")) { CheckShaderWindows(null); return 0; }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var root = Path.GetFullPath(args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal)) ?? ".");
            var output = Path.Combine(root, "tools", "AEther.Smoke", "artifacts");
            Directory.CreateDirectory(output);
            var shaderDirectory = Path.Combine(output, "Shaders-" + Guid.NewGuid().ToString("N"));
            CopyShaderTree(Path.Combine(root, "AEther.Vortice", "Shaders"), shaderDirectory);
            if (args.Contains("--windows-watcher-only")) { CheckShaderWindows(shaderDirectory); return 0; }
            using var form = new Form { ClientSize = new(640, 360), ShowInTaskbar = false };
            if (args.Contains("--output-only")) { CheckColorOutput(form.Handle, shaderDirectory, output); return 0; }
            if (args.Contains("--fluid-only")) { CheckFluid(form.Handle, shaderDirectory, output); return 0; }
            if (args.Contains("--shader-authoring-only") || !args.Any(arg => arg.StartsWith("--", StringComparison.Ordinal)))
            {
                CheckShaderAuthoring(form.Handle, shaderDirectory, output);
                CheckShaderWindows(shaderDirectory);
                return 0;
            }
            throw new ArgumentException("Unsupported smoke mode.");
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void CheckColorOutput(IntPtr window, string shaders, string output)
    {
        var fixture = Path.Combine(output, "OutputCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        CopyShaderTree(shaders, fixture);
        File.WriteAllText(Path.Combine(fixture, "Scenes", "Spectrum.hlsl"), """
            float4 PS(PSin input) : SV_Target
            {
                float value = input.UV.y < .5 ? .002 + .078 * input.UV.x
                    : (input.UV.x < .2 ? 0 : (input.UV.x > .8 ? 1 : .041));
                return float4(value.xxx, 1);
            }
            """);
        const int width = 2048, height = 64;
        foreach (bool highPrecision in new[] { true, false })
        {
            using var renderer = new Renderer(window, width, height, 16, 64, 1,
                shaderDirectory: fixture, highPrecisionOutput: highPrecision);
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
            var presentPath = Path.Combine(fixture, "Common", "Postprocessing.hlsl");
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
        foreach (var scene in new[] { "MandelFluid" })
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
            File.WriteAllText(Path.Combine(output, scene.ToLowerInvariant() + "-fluid-state.sha256"), state);
            if (reference != null && state != reference) throw new Exception($"{scene} differs from the shared fluid reference.");
            reference = state;
            Save(renderer.ReadPixels(false), 1280, 720, Path.Combine(output, scene.ToLowerInvariant() + "-shared-fluid.png"));
            foreach (var size in new[] { new Size(191, 97), new Size(97, 191), new Size(16, 16) })
            {
                renderer.Resize(size.Width, size.Height);
                renderer.Render(false);
                if (renderer.Fluid.Width != 512 || renderer.Fluid.Height != 512)
                    throw new Exception("Shared fluid grid changed with viewport size.");
            }
            Console.WriteLine($"{scene}: shared fluid material, projection, silence and resize checks passed.");
        }
    }

    sealed class FluidSmokeClock : TimeProvider
    {
        public long Frame;
        public override long TimestampFrequency => 60;
        public override long GetTimestamp() => Frame;
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

    sealed class SyntheticSource(Action started) : SampleSource, ITimedSampleSource
    {
        public event TimedSampleDataHandler? TimedDataAvailable;
        public override SampleFormat Format { get; } = new(SampleType.Float32.Instance, 48000, 2);
        public override void Start()
        {
            started();
            var data = new byte[480 * 8];
            TimedDataAvailable?.Invoke(this, data, new AudioPacketTiming(
                System.Diagnostics.Stopwatch.GetTimestamp() - AudioClock.Frames(480, 48000), 0, AudioTimestampQuality.Device));
            DataAvailable?.Invoke(this, data);
        }
        public override void Stop() => Stopped?.Invoke(this, null);
        public override void Dispose() { }
    }
}

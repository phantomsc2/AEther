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

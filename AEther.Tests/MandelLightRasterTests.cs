using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public class MandelLightRasterTests
{
    [Test, Category("GPU")]
    public void RasterVisibilitySurvivesGeometryChangesResizeReloadAndDisablingLights()
    {
        using var shaders = new ShaderTree();
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 80, 48, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4");
        Set(renderer, "Air.Density", 0f);
        byte[] original = Capture();
        Assert.That(Rgb(original).Any(x => x > 20), Is.True);
        Set(renderer, "Filter.Enabled", false);
        Assert.That(Capture(), Is.EqualTo(original), "The filter must not change opaque surfaces in vacuum.");
        Set(renderer, "Filter.Enabled", true);
        Set(renderer, "Shape.Seed", .75f);
        Assert.That(Capture(), Is.Not.EqualTo(original));
        Set(renderer, "Shape.Seed", .25f);
        Assert.That(Capture(), Is.EqualTo(original));
        Set(renderer, "Light.Phase", .5f);
        Assert.That(Capture(), Is.Not.EqualTo(original));
        Set(renderer, "Light.Phase", 0f);
        Assert.That(Capture(), Is.EqualTo(original));
        Set(renderer, "Quality.ShadowResolution", 1f); Capture();
        Set(renderer, "Quality.ShadowResolution", .5f);
        Assert.That(Capture(), Is.EqualTo(original));
        Set(renderer, "Shape.Enabled", false);
        Assert.That(Rgb(Capture()), Is.All.Zero, "Empty vacuum must contain no residual surface radiance.");
        Set(renderer, "Shape.Enabled", true);
        Assert.That(Capture(), Is.EqualTo(original));
        renderer.Resize(96, 54); Capture(); renderer.Resize(80, 48);
        Assert.That(Capture(), Is.EqualTo(original));
        Assert.That(renderer.ReloadShaders(), Is.True, renderer.ShaderError);
        Assert.That(Capture(), Is.EqualTo(original));
        Set(renderer, "Air.Density", .4f);
        Assert.That(Rgb(Capture()).Any(x => x > 20), Is.True);
        foreach (string group in new[] { "Light", "Light2", "Light3", "Light4" }) Set(renderer, group + ".Enabled", false);
        for (int frame = 0; frame < 3; frame++)
            Assert.That(Rgb(Capture()), Is.All.Zero, "Disabling all lights must remove both surface and air radiance.");
        Set(renderer, "View.ShowShadows", true);
        Set(renderer, "View.ShadowLight", 1f);
        Assert.That(Rgb(Capture()).Any(x => x > 20), Is.True, "A disabled light must still have a valid diagnostic map.");
        byte[] Capture() { renderer.Render(false); return renderer.ReadPixels(false); }
    }

    [TestCase(0f), TestCase(.5f), TestCase(1f), Category("GPU")]
    public void InfiniteAtmosphereSamplingAgreesWithNumericalSingleScattering(float opticalWeight)
    {
        const int width = 64, height = 36;
        using var shaders = new ShaderTree();
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, width, height, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4");
        Set(renderer, "Shape.Enabled", false);
        foreach (string group in new[] { "Light2", "Light3", "Light4" }) Set(renderer, group + ".Enabled", false);
        foreach (char axis in "xyz")
        {
            Set(renderer, "Air.Scattering." + axis, .5f);
            Set(renderer, "Air.Absorption." + axis, 0f);
            Set(renderer, "Light.Color." + axis, 1f);
        }
        Set(renderer, "Air.Density", .4f);
        Set(renderer, "Light.Intensity", .15f);
        Set(renderer, "Light.Radius", 1f);
        Set(renderer, "View.CameraDistance", .6f);
        Set(renderer, "Quality.Samples", 1f);
        Set(renderer, "Sampling.OpticalDepthWeight", opticalWeight);
        Set(renderer, "Filter.Enabled", false); // Test the estimator independently of spatial reconstruction.
        renderer.Render(false);
        byte[] pixels = renderer.ReadPixels(false);
        Vector3 forward = Vector3.Normalize(new(-9, -6, 13.5f));
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, forward), origin = -24 * forward;
        Vector3 light = new((float)(11.8 * Math.Cos(-.8)), (float)(3 * Math.Sin(-.26)), (float)(11.8 * Math.Sin(-.8)));
        double squared = 0; int count = 0;
        for (int y = 1; y < height; y += 3)
        for (int x = 1; x < width; x += 3)
        {
            float sx = (2 * (x + .5f) / width - 1) * width / height;
            float sy = 1 - 2 * (y + .5f) / height;
            Vector3 direction = Vector3.Normalize(2 * forward + sx * right + sy * up);
            // Independent Simpson quadrature over t=32*u/(1-u), covering [0,infinity).
            // No shadow map, proposal PDF or GPU sampling code enters the reference.
            const int steps = 8192;
            double sum = 0;
            for (int k = 0; k <= steps; k++)
            {
                if (k == steps) continue; // The exponentially attenuated endpoint limit is zero.
                double u = k / (double)steps, t = 32 * u / (1 - u);
                double dx = light.X - origin.X - t * direction.X;
                double dy = light.Y - origin.Y - t * direction.Y;
                double dz = light.Z - origin.Z - t * direction.Z;
                double r2 = dx * dx + dy * dy + dz * dz, r = Math.Sqrt(r2);
                double mu = (dx * direction.X + dy * direction.Y + dz * direction.Z) / r;
                const double sigma = .3 * .4 * .4 * .5;
                double value = sigma * (3000 * .15 * .15) * 3 * (1 + mu * mu) / (16 * Math.PI)
                    * Math.Exp(-sigma * (t + r)) / (r2 + .25) * 32 / ((1 - u) * (1 - u));
                sum += (k == 0 ? 1 : k % 2 == 0 ? 2 : 4) * value;
            }
            double radiance = sum / (3 * steps);
            double mapped = 1 - Math.Exp(-radiance * Math.Pow(2, -.6));
            double expected = mapped <= .0031308 ? 12.92 * mapped : 1.055 * Math.Pow(mapped, 1 / 2.4) - .055;
            double observed = pixels[4 * (y * width + x)] / 255.0;
            squared += (observed - expected) * (observed - expected); count++;
        }
        double rmse = Math.Sqrt(squared / count);
        TestContext.Progress.WriteLine($"Optical weight {opticalWeight}: display-RGB RMSE {rmse:F5} against infinite-medium quadrature.");
        Assert.That(rmse, Is.LessThan(.01), "Proposal changes must preserve the single-scattering integral within sampling error.");
    }

    [Test, Category("GPU")]
    public void AirlightFilterRejectsDepthBoundariesAndAveragesOnlyItsThreeByThreeNeighborhood()
    {
        using var shaders = new ShaderTree(filterProbe: true);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 16, 9, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4");
        Set(renderer, "Shape.Enabled", false);
        Set(renderer, "View.Exposure", .5f);
        Set(renderer, "Filter.Enabled", false);
        renderer.Render(false); byte[] raw = renderer.ReadPixels(false);
        Pixel(raw, 4, 4, new(4.1f, 4.2f, 4.3f));
        Pixel(raw, 3, 4, new(.1f, .2f, .3f));
        Set(renderer, "Filter.Enabled", true);
        renderer.Render(false); byte[] filtered = renderer.ReadPixels(false);
        Pixel(filtered, 4, 4, new(1.1f, 1.2f, 1.3f));
        Pixel(filtered, 3, 4, new(.6f, .7f, .8f));
        Pixel(filtered, 3, 3, new(.35f, .45f, .55f));
        Pixel(filtered, 2, 4, new(.1f, .2f, .3f));
        Pixel(filtered, 7, 4, new(.1f, .2f, .3f)); // Sky to foreground: no leakage.
        Pixel(filtered, 8, 4, new(2f, .5f, .25f));
        Pixel(filtered, 11, 4, new(2f, .5f, .25f)); // Distinct finite depths: no leakage.
        Pixel(filtered, 12, 4, new(.25f, 1f, 2f));
        Set(renderer, "Filter.Enabled", false);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(raw), "Filter toggles must not leave persistent state.");

        static void Pixel(byte[] pixels, int x, int y, Vector3 expected)
        {
            for (int c=0;c<3;c++)
            {
                double value=1-Math.Exp(-expected[c]);
                double encoded=value<=.0031308 ? 12.92*value : 1.055*Math.Pow(value,1/2.4)-.055;
                Assert.That(Math.Abs(pixels[4*(y*16+x)+c]-255*encoded), Is.LessThan(1.5), $"Pixel ({x},{y}), channel {c}.");
            }
        }
    }

    static System.Collections.Generic.IEnumerable<byte> Rgb(byte[] pixels) => pixels.Where((_, i) => i % 4 != 3);
    static ShaderParameter Find(Renderer renderer, string path)
    {
        var items = renderer.ActiveShader!.Parameters!.Items;
        ShaderParameter result = null!;
        foreach (string part in path.Split('.')) { result = items.Single(p => p.Name == part); items = result.Children; }
        return result;
    }
    static void Set(Renderer renderer, string path, float value) => renderer.ActiveShader!.Parameters!.SetFloat(Find(renderer, path), value);
    static void Set(Renderer renderer, string path, bool value) => renderer.ActiveShader!.Parameters!.SetBoolean(Find(renderer, path), value);
    sealed class FixedClock : TimeProvider { public override long TimestampFrequency => 60; public override long GetTimestamp() => 0; }
    sealed class ShaderTree : IDisposable
    {
        readonly string root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        internal string Path { get; }
        internal ShaderTree(bool filterProbe = false)
        {
            string source = null!;
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "AEther.Vortice", "Shaders");
                if (Directory.Exists(candidate)) { source = candidate; break; }
            }
            if (source == null) throw new DirectoryNotFoundException("Cannot locate shader sources.");
            Path = System.IO.Path.Combine(root, "AEther-raster-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Common"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Scenes"));
            foreach (string file in new[] { "Inputs.hlsl", "Present.hlsl" })
                File.Copy(System.IO.Path.Combine(source, "Common", file), System.IO.Path.Combine(Path, "Common", file));
            string scene=File.ReadAllText(System.IO.Path.Combine(source, "Scenes", "MandelLightv4.hlsl"));
            if (filterProbe)
                scene=scene.Replace("float4 RasterAirlightPS(", "float4 OriginalRasterAirlightPS(") + """

                    float4 RasterAirlightPS(PSin input) : SV_Target
                    {
                        int2 p=(int2)input.Position.xy;
                        float3 color=p.x<8 ? float3(.1,.2,.3) : p.x<12 ? float3(2,.5,.25) : float3(.25,1,2);
                        if (all(p==int2(4,4))) color+=4;
                        return float4(color,p.x<8 ? 0 : p.x<12 ? 20 : 40);
                    }
                    """;
            File.WriteAllText(System.IO.Path.Combine(Path, "Scenes", "MandelLightv4.hlsl"),scene);
        }
        public void Dispose()
        {
            if (!System.IO.Path.GetFullPath(Path).StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Raster shader cleanup escaped the temporary directory.");
            Directory.Delete(Path, true);
        }
    }
    sealed class HiddenWindow : IDisposable
    {
        internal IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "AEther raster regression", 0x80000000,
            0, 0, 96, 54, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        internal HiddenWindow() { if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(); }
        public void Dispose() => DestroyWindow(Handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr handle);
    }
}

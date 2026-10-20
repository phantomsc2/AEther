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
        Set(renderer, "Lights.RingRadius", .3f);
        Assert.That(Capture(), Is.Not.EqualTo(original));
        Set(renderer, "Lights.RingRadius", .6f);
        Assert.That(Capture(), Is.EqualTo(original));
        Set(renderer, "Quality.ShadowResolution", .5f); Capture();
        Set(renderer, "Quality.ShadowResolution", 1f);
        Assert.That(Capture(), Is.EqualTo(original));
        Set(renderer, "Quality.HardwarePCF", false); Capture();
        Set(renderer, "Quality.HardwarePCF", true);
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
        Set(renderer, "Lights.Enabled", false);
        for (int frame = 0; frame < 3; frame++)
            Assert.That(Rgb(Capture()), Is.All.Zero, "Disabling all lights must remove both surface and air radiance.");
        Set(renderer, "View.ShowShadows", true);
        Set(renderer, "View.ShadowLight", 1f);
        Assert.That(Rgb(Capture()).Any(x => x > 20), Is.True, "A disabled light must still have a valid diagnostic map.");
        byte[] Capture() { FeedTransient(renderer); renderer.Render(false); return renderer.ReadPixels(false); }
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
        Set(renderer, "Quality.HalfResolutionAirlight", false);
        Set(renderer, "Sampling.RandomLight", false);
        Set(renderer, "Lights.Count", 0f);
        Set(renderer, "Lights.Gain", .5f);
        Set(renderer, "Shape.Enabled", false);
        foreach (char axis in "xyz")
        {
            Set(renderer, "Air.Scattering." + axis, .5f);
            Set(renderer, "Air.Absorption." + axis, 0f);
        }
        Set(renderer, "Air.Density", .4f);
        Set(renderer, "Lights.Radius", 1f);
        Set(renderer, "View.CameraDistance", .6f);
        Set(renderer, "Quality.Samples", 1f);
        Set(renderer, "Sampling.OpticalDepthWeight", opticalWeight);
        Set(renderer, "Filter.Enabled", false); // Test the estimator independently of spatial reconstruction.
        FeedTransient(renderer, .0225);
        renderer.Render(false);
        byte[] pixels = renderer.ReadPixels(false);
        Vector3 forward = Vector3.Normalize(new(-9, -6, 13.5f));
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, forward), origin = -24 * forward;
        Vector3 light = 5.7f * up;
        double red = .5 + .5 * Math.Cos(2 * Math.PI * (.85 + .04)); // RingPalette at upper treble pole.
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
                double value = sigma * (3000 * .0225 * red) * 3 * (1 + mu * mu) / (16 * Math.PI)
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
        Set(renderer, "Quality.HalfResolutionAirlight", false);
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

    [Test, Category("GPU")]
    public void HardwarePcfMatchesBilinearDepthComparisonsAcrossCubeFaces()
    {
        using var shaders = new ShaderTree(shadowProbe: true);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 256, 128, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4");
        foreach (float resolution in new[] { 0f, .5f, 1f })
        {
            Set(renderer, "Quality.ShadowResolution", resolution);
            FeedTransient(renderer);
            renderer.Render(false);
            byte[] pixels=renderer.ReadPixels(false);
            Assert.That(pixels.Where((_, i)=>i%4==0), Is.All.Zero,
                "Hardware PCF must interpolate comparisons, and every face-edge footprint must fit inside its rendered guard.");
            Assert.That(pixels.Where((_, i)=>i%4==1).Count(v=>v>0), Is.GreaterThan(20),
                "The probe must exercise fractional shadow coverage, not only fully lit/shadowed texels.");
        }
    }

    static System.Collections.Generic.IEnumerable<byte> Rgb(byte[] pixels) => pixels.Where((_, i) => i % 4 != 3);
    static void FeedTransient(Renderer renderer, double level = .02)
    {
        int bands = renderer.Audio.Bands;
        var spectrum = new double[8 * bands];
        for (int i = 1; i < spectrum.Length; i += 4) spectrum[i] = level;
        renderer.PostAudio(new(spectrum, bands * 4, DateTime.UnixEpoch), 2);
    }
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
        internal ShaderTree(bool filterProbe = false, bool shadowProbe = false)
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
            if (shadowProbe)
                scene=scene.Replace("float4 PS(PSin input)", "float4 OriginalPS(PSin input)") + """

                    float4 PS(PSin input) : SV_Target
                    {
                        // Include exact X/Z face seams as well as generic directions.
                        float angle=(input.Position.x-.5)*2*Pi/256;
                        float3 d=20*normalize(float3(cos(angle),2*input.UV.y-1,sin(angle)));
                        float3 p=LightPosition(0)+d;
                        float actual=Visibility(0,p,0);
                        int face; float2 uv; CubeAddress(d,face,uv);
                        uint w,h,layers; RasterShadows.GetDimensions(w,h,layers);
                        uv=.5+(uv-.5)*((w-2.0)/w);
                        float2 at=uv*w-.5,f=frac(at); int2 base=(int2)floor(at);
                        float z=max(abs(d.x),max(abs(d.y),abs(d.z)))-ShadowBias();
                        float reference=saturate(ProjectDepth(z)/z),expected=0;
                        [unroll] for (int y=0;y<2;++y) [unroll] for (int x=0;x<2;++x)
                        {
                            float stored=RasterShadows.Load(int4(base+int2(x,y),face,0));
                            expected+=(x ? f.x : 1-f.x)*(y ? f.y : 1-f.y)*(reference<=stored ? 1 : 0);
                        }
                        bool invalid=abs(actual-expected)>.012 || !isfinite(actual)
                            || any(base<0) || any(base+1>=(int)w);
                        // Keep the real raster graph active, including camera resources.
                        invalid=invalid || RasterSurface.Load(int3((int2)input.Position.xy,0)).a<0;
                        return float4(invalid ? 1 : 0,actual>.01 && actual<.99 ? 1 : 0,0,1);
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

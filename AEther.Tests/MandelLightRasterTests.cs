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
    public void TonalRingUsesStructureWithoutShadowDrawsAndMatchesTheStrobeLayout()
    {
        using var shaders = new ShaderTree();
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 96, 54, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4");
        var passes = new System.Collections.Generic.List<string>();
        renderer.TransportPassMarker = (name, start) => { if (start) passes.Add(name); };
        WaitForTonalLookup(renderer);
        Assert.That(Rgb(renderer.ReadPixels(false)).Any(v => v > 10), Is.True);
        Assert.That(passes.Any(name => name.StartsWith("Raster light ")), Is.False,
            "Structure-only lights must not render shadow cubemaps.");
        Set(renderer, "TonalLights.Enabled", false);
        FeedTonal(renderer); renderer.Render(false);
        Assert.That(Rgb(renderer.ReadPixels(false)), Is.All.Zero, "S must not illuminate the transient ring.");
        Set(renderer, "TonalLights.Enabled", true);
        FeedTonal(renderer, tonal: 0); renderer.Render(false);
        Assert.That(Rgb(renderer.ReadPixels(false)), Is.All.Zero, "Live tonal light must not retain a previous spectrum.");

        FeedTonal(renderer, transient: .02); renderer.Render(false);
        // Inspect the actual uploaded layout, including both active lists. These
        // constants intentionally match the shader/host ABI, not a second ring generator.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        object transport = typeof(Renderer).GetField("rasterTransport", flags)!.GetValue(renderer)!;
        var packed = (Vector4[])transport.GetType().GetField("lightData", flags)!.GetValue(transport)!;
        const int capacity=RasterLightTransport.MaximumLights, strobeRadiance=1+capacity, tonalPosition=1+2*capacity+capacity/4, tonalRadiance=tonalPosition+capacity;
        Assert.That(packed[0], Is.EqualTo(new Vector4(60, 60, 60, 60)));
        Vector3 forward = Vector3.Normalize(new(-9, -6, 13.5f));
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        for (int i = 0; i < 60; i++)
        {
            Vector3 strobe = new(packed[1+i].X, packed[1+i].Y, packed[1+i].Z);
            Vector3 tonal = new(packed[tonalPosition+i].X, packed[tonalPosition+i].Y, packed[tonalPosition+i].Z);
            Assert.That(tonal.Y, Is.Zero);
            Assert.That(tonal.Length(), Is.EqualTo(strobe.Length()).Within(1e-5));
            Assert.That(Vector3.Dot(tonal, right), Is.EqualTo(Vector3.Dot(strobe, right)).Within(1e-5));
            Vector4 difference = packed[tonalRadiance+i] - .75f * packed[strobeRadiance+i];
            Assert.That(difference.Length(), Is.LessThan(.001), "Equal S/T amplitudes must give the default 75% tonal intensity.");
        }
        Vector4 strobeBefore=packed[strobeRadiance],tonalBefore=packed[tonalRadiance];
        Set(renderer,"Lights.Gain",.5f);
        FeedTonal(renderer,transient:.02);renderer.Render(false);
        Assert.That(packed[tonalRadiance],Is.EqualTo(tonalBefore),"Strobe gain must not attenuate tonal lights.");
        Assert.That(packed[strobeRadiance].W,Is.LessThan(strobeBefore.W));
        Vector4 strobeAttenuated=packed[strobeRadiance];
        Set(renderer,"TonalLights.Gain",.5f);
        FeedTonal(renderer,transient:.02);renderer.Render(false);
        Assert.That(packed[strobeRadiance],Is.EqualTo(strobeAttenuated),"Tonal gain must not attenuate strobes.");
        Assert.That(packed[tonalRadiance].W,Is.LessThan(tonalBefore.W));
        Set(renderer, "Lights.Enabled", false);
        FeedTonal(renderer, transient: .02); renderer.Render(false);
        Assert.That(packed[0].Y,Is.Zero);
        Assert.That(packed[0].W,Is.EqualTo(60));
        Assert.That(Rgb(renderer.ReadPixels(false)).Any(v=>v>10),Is.True,"Disabling strobes must leave tonal light enabled.");
        Set(renderer,"TonalLights.Enabled",false);
        FeedTonal(renderer,transient:.02);renderer.Render(false);
        Assert.That(Rgb(renderer.ReadPixels(false)), Is.All.Zero, "Disabling both rings must remove all light.");
        Set(renderer,"Lights.Enabled",true);
        FeedTonal(renderer,transient:.02);renderer.Render(false);
        Assert.That(packed[0].Y,Is.EqualTo(60));
        Assert.That(packed[0].W,Is.Zero,"Disabling tonal lights must leave strobes enabled.");
    }

    [TestCase(.4f, 1f), TestCase(.65f, .25f), Category("GPU")]
    public void TonalLookupAgreesWithIndependentInfiniteMediumQuadrature(float density, float radius)
    {
        const int width = 64, height = 36;
        using var shaders = new ShaderTree(); using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, width, height, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4");
        Set(renderer, "View.CameraSpeed", 0); Set(renderer, "View.CameraDistance", .6f);
        Set(renderer, "Lights.Count", 0); Set(renderer, "Lights.Gain", .5f); Set(renderer, "Lights.Radius", radius);
        Set(renderer,"TonalLights.Gain",.5f);
        Set(renderer, "Shape.Enabled", false); Set(renderer, "Air.Density", density);
        Set(renderer, "Quality.HalfResolutionAirlight", false); Set(renderer, "Filter.Enabled", false);
        foreach (char axis in "xyz") { Set(renderer, "Air.Scattering." + axis, .5f); Set(renderer, "Air.Absorption." + axis, 0f); }
        WaitForTonalLookup(renderer, .0225);
        byte[] pixels = renderer.ReadPixels(false);
        Vector3 forward = Vector3.Normalize(new(-9, -6, 13.5f));
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Cross(right, forward), origin = -24 * forward;
        Vector3 light = 5.7f * Vector3.Cross(Vector3.UnitY, right);
        double red = .5 + .5 * Math.Cos(2 * Math.PI * (.85 + .04));
        double sigma = .3 * density * density * .5, h = .01 + .49 * radius * radius;
        double squared = 0; int count = 0;
        for (int y = 1; y < height; y += 5)
        for (int x = 1; x < width; x += 7)
        {
            float sx = (2 * (x+.5f) / width - 1) * width / height, sy = 1 - 2 * (y+.5f) / height;
            Vector3 direction = Vector3.Normalize(2 * forward + sx * right + sy * up);
            const int steps = 8192; double sum = 0;
            for (int k = 0; k < steps; k++)
            {
                double u = k/(double)steps, t = 32*u/(1-u);
                double dx = light.X-origin.X-t*direction.X, dy = light.Y-origin.Y-t*direction.Y, dz = light.Z-origin.Z-t*direction.Z;
                double r2 = dx*dx+dy*dy+dz*dz, r = Math.Sqrt(r2);
                double mu = (dx*direction.X+dy*direction.Y+dz*direction.Z)/r;
                double value = sigma * (.75*3000*.0225*red) * 3*(1+mu*mu)/(16*Math.PI)
                    * Math.Exp(-sigma*(t+r))/(r2+h*h) * 32/((1-u)*(1-u));
                sum += (k==0 ? 1 : k%2==0 ? 2 : 4)*value;
            }
            double mapped = 1-Math.Exp(-sum/(3*steps)*Math.Pow(2,-.6));
            double expected = mapped<=.0031308 ? 12.92*mapped : 1.055*Math.Pow(mapped,1/2.4)-.055;
            squared += Math.Pow(pixels[4*(y*width+x)]/255.0-expected,2); count++;
        }
        double rmse = Math.Sqrt(squared/count);
        TestContext.Progress.WriteLine($"Tonal LUT: density={density}, radius={radius}, display-RGB RMSE={rmse:F6}");
        Assert.That(rmse, Is.LessThan(.005));
    }

    [Test, Category("GPU")]
    public void TonalLookupInvalidatesOnCoefficientEditsAndSurvivesResizeAndReload()
    {
        using var shaders = new ShaderTree(); using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 81, 47, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4"); Set(renderer, "Air.Density", .4f); Set(renderer, "Lights.Radius", 1);
        WaitForTonalLookup(renderer); byte[] original = renderer.ReadPixels(false);
        Set(renderer, "Air.Density", .7f); FeedTonal(renderer); renderer.Render(false);
        Assert.That(renderer.UnshadowedAirlightReady, Is.False, "A previous medium's table must not be sampled.");
        WaitForTonalLookup(renderer); Assert.That(renderer.ReadPixels(false), Is.Not.EqualTo(original));
        Set(renderer, "Air.Density", .4f); FeedTonal(renderer); renderer.Render(false);
        Assert.That(renderer.UnshadowedAirlightReady, Is.True, "Returning to a cached medium must reuse its table.");
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
        Set(renderer, "Lights.Radius", .25f); FeedTonal(renderer); renderer.Render(false);
        Assert.That(renderer.UnshadowedAirlightReady, Is.False);
        WaitForTonalLookup(renderer); byte[] edited = renderer.ReadPixels(false);
        renderer.Resize(97, 55); FeedTonal(renderer); renderer.Render(false);
        renderer.Resize(81, 47); FeedTonal(renderer); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(edited));
        Assert.That(renderer.ReloadShaders(), Is.True, renderer.ShaderError);
        FeedTonal(renderer); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(edited));
    }

    static void FeedTonal(Renderer renderer, double tonal = .02, double transient = 0)
    {
        int bands = renderer.Audio.Bands; var spectrum = new double[8*bands];
        for (int i = 0; i < spectrum.Length; i += 4) { spectrum[i] = tonal; spectrum[i+1] = transient; }
        renderer.PostAudio(new(spectrum, bands*4, DateTime.UnixEpoch), 2);
    }
    static void WaitForTonalLookup(Renderer renderer, double level = .02)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            FeedTonal(renderer, level); renderer.Render(false);
            if (watch.Elapsed.TotalSeconds > 10) Assert.Fail("Tonal lookup generation timed out.");
            if (!renderer.UnshadowedAirlightReady) Thread.Sleep(1);
        } while (!renderer.UnshadowedAirlightReady);
    }

    [Test, Category("GPU")]
    public void RasterVisibilitySurvivesGeometryChangesResizeReloadAndDisablingLights()
    {
        using var shaders = new ShaderTree();
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 80, 48, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4");
        Set(renderer,"Lights.Shadows",true);
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

    [TestCase(0f, false, false, false), TestCase(.5f, false, false, false), TestCase(1f, false, false, false)]
    [TestCase(0f, true, false, false), TestCase(.5f, true, false, false), TestCase(1f, true, false, false)]
    [TestCase(0f, true, true, false), TestCase(.5f, true, true, false), TestCase(1f, true, true, false)]
    [TestCase(0f, true, false, true), TestCase(.5f, true, false, true), TestCase(1f, true, false, true)]
    [TestCase(0f, true, true, true), TestCase(.5f, true, true, true), TestCase(1f, true, true, true), Category("GPU")]
    [TestCase(0f, true, false, false, 4), TestCase(.5f, true, false, false, 4), TestCase(1f, true, false, false, 4)]
    [TestCase(0f, true, true, false, 4), TestCase(.5f, true, true, false, 4), TestCase(1f, true, true, false, 4)]
    [TestCase(.5f, true, false, true, 4), TestCase(.5f, true, true, true, 4)]
    public void InfiniteAtmosphereSamplingAgreesWithNumericalSingleScattering(float opticalWeight, bool directional, bool randomLight, bool finite, int lightCount=1)
    {
        const int width = 64, height = 36;
        using var shaders = new ShaderTree(finiteAirlight: finite);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, width, height, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new FixedClock());
        renderer.SelectScene("MandelLightv4");
        Set(renderer,"Lights.Shadows",true); // Exercise the sampled path rather than the unshadowed LUT.
        Set(renderer, "Quality.HalfResolutionAirlight", false);
        Set(renderer, "Sampling.RandomLight", randomLight);
        Set(renderer, "Lights.Count", (lightCount-1)/(float)(RasterLightTransport.MaximumLights-1));
        Set(renderer, "Lights.Gain", .5f);
        Set(renderer, "Lights.Directional", directional);
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
        var sources=Enumerable.Range(0,lightCount).Select(i =>
        {
            double a=2*Math.PI*i/lightCount;
            float x=(float)Math.Sin(a),y=(float)Math.Cos(a);
            double f=Math.Atan2(Math.Abs(x),-y)/Math.PI;
            return (Position:5.7f*(x*right+y*up), Red:.5+.5*Math.Cos(2*Math.PI*(.85*f+.04)));
        }).ToArray();
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
                if (!finite && k == steps) continue; // The exponentially attenuated endpoint limit is zero.
                double u = k / (double)steps, t = finite ? 24*u : 32*u/(1-u);
                double value=0;
                foreach(var source in sources)
                {
                    Vector3 light=source.Position;
                    double dx = light.X - origin.X - t * direction.X;
                    double dy = light.Y - origin.Y - t * direction.Y;
                    double dz = light.Z - origin.Z - t * direction.Z;
                    double r2 = dx * dx + dy * dy + dz * dz, r = Math.Sqrt(r2);
                    if (directional && (light.X*dx+light.Y*dy+light.Z*dz)/(light.Length()*r)<Math.Cos(Math.PI/6)) continue;
                    double mu = (dx * direction.X + dy * direction.Y + dz * direction.Z) / r;
                    const double sigma = .3 * .4 * .4 * .5;
                    value += sigma * (3000 * .0225 * source.Red) * 3 * (1 + mu * mu) / (16 * Math.PI)
                        * Math.Exp(-sigma * (t + r)) / (r2 + .25) * (finite ? 24 : 32/((1-u)*(1-u)));
                }
                sum += (k == 0 ? 1 : k % 2 == 0 ? 2 : 4) * value;
            }
            double radiance = sum / (3 * steps);
            double mapped = 1 - Math.Exp(-radiance * Math.Pow(2, -.6));
            double expected = mapped <= .0031308 ? 12.92 * mapped : 1.055 * Math.Pow(mapped, 1 / 2.4) - .055;
            double observed = pixels[4 * (y * width + x)] / 255.0;
            squared += (observed - expected) * (observed - expected); count++;
        }
        double rmse = Math.Sqrt(squared / count);
        TestContext.Progress.WriteLine($"Optical {opticalWeight}, cone {directional}, random {randomLight}, finite {finite}, lights {lightCount}: display-RGB RMSE {rmse:F5} against independent quadrature.");
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
        Set(renderer,"Lights.Shadows",true);
        Set(renderer,"Lights.Directional",false); // This probe specifically checks cubemap seams.
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
        internal ShaderTree(bool filterProbe = false, bool shadowProbe = false, bool finiteAirlight = false)
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
            if (finiteAirlight)
                scene=scene.Replace("float end=hit ? LinearDepth(z)/dot(direction,forward) : 0;", "hit=true; float end=24;");
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

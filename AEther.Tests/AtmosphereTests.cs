using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AEther.Vortice;
using NUnit.Framework;
using Vortice.Direct3D11;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    static float[] AtmosphereRead(Renderer renderer, ID3D11Texture2D texture)
    {
        var device = PointField<ID3D11Device>(renderer, "device");
        var context = PointField<ID3D11DeviceContext>(renderer, "context");
        var desc = texture.Description;
        desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, texture);
        var map = context.Map(staging, 0, MapMode.Read, global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            var values = new float[desc.Width * desc.Height * 4];
            for (int y = 0; y < desc.Height; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, values, y * (int)desc.Width * 4, (int)desc.Width * 4);
            return values;
        }
        finally { context.Unmap(staging, 0); }
    }

    // Independent double-precision reference: direct path quadrature, with no LUT lookups.
    const double AtmosphereR = 6360, AtmosphereTop = AtmosphereR+10*8;
    static double AtmosphereReferenceAltitude(double v, double scale)
    {
        double q=v*v*10/11;
        return scale*q/(1-q);
    }
    static double AtmosphereReferenceDistance(double r, double mu, double top = AtmosphereTop) =>
        -r * mu + Math.Sqrt(r * r * mu * mu + top * top - r * r);
    static double AtmosphereReferenceColumn(double r, double mu, double distance, double scaleHeight = 8)
    {
        const int steps = 2048;
        double sum = 0, ds = distance / steps;
        for (int i = 0; i <= steps; i++)
        {
            double t = i * ds, h = Math.Sqrt(r * r + t * (2 * r * mu + t)) - AtmosphereR;
            sum += (i == 0 || i == steps ? 1 : i % 2 == 0 ? 2 : 4) * Math.Exp(-h / scaleHeight);
        }
        return sum * ds / 3;
    }
    static float AtmosphereAnisotropySlider(double g) => (float)(Math.Log(1-g)/(2*Math.Log(.05)));
    static float AtmosphereStrengthSlider(double strength) => (float)Math.Pow(strength,1/1.7958592832);
    static double AtmosphereReferencePhase(double c, double g, double strength)
    {
        double rayleigh = 3*(1+c*c)/(16*Math.PI);
        double hg = (1-g*g)/(4*Math.PI*Math.Pow(1+g*g-2*g*c,1.5));
        return (rayleigh+strength*hg)/(1+strength);
    }
    static double[] AtmosphereReferenceSky(double azimuth, double elevation, double sunAltitude,
        double sunAzimuth = 0, double intensity = 20, double cutoff = 10, int steps = 4096)
    {
        double vy = Math.Sin(elevation), vz = Math.Cos(elevation) * Math.Cos(azimuth-sunAzimuth);
        double sy = Math.Sin(sunAltitude), sz = Math.Cos(sunAltitude), c = vy * sy + vz * sz;
        double top = AtmosphereR+cutoff*8;
        double ds = AtmosphereReferenceDistance(AtmosphereR, vy, top) / steps;
        double column = 0, phase = AtmosphereReferencePhase(c,.855,.25);
        double[] beta = [.0058, .0135, .0331], result = new double[3];
        for (int i = 0; i < steps; i++)
        {
            double t = (i + .5) * ds, r = Math.Sqrt(AtmosphereR * AtmosphereR + t * (2 * AtmosphereR * vy + t));
            double density = Math.Exp(-(r - AtmosphereR) / 8), mu = (AtmosphereR * sy + t * c) / r;
            bool shadow = mu < 0 && r * r * mu * mu > r * r - AtmosphereR * AtmosphereR;
            if (!shadow)
            {
                double sunColumn = AtmosphereReferenceColumn(r, mu, AtmosphereReferenceDistance(r, mu, top));
                for (int channel = 0; channel < 3; channel++)
                    result[channel] += intensity * phase * beta[channel] * density * ds *
                        Math.Exp(-beta[channel] * (column + .5 * density * ds + sunColumn));
            }
            column += density * ds;
        }
        return result;
    }

    [TestCase(30), TestCase(0), TestCase(-3), Category("GPU")]
    public void AtmosphereLutsMatchIndependentTransport(double altitude)
    {
        using var shaders = new SceneShaders("Atmosphere");
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 1280, 720, 8, 60,
            shaderDirectory: shaders.Path, initialScene: shaders.Scene);
        PointSet(renderer, "Sun.Altitude", (float)((altitude + 90) / 180));
        PointSet(renderer, "Sun2.Intensity", 0);
        renderer.Render(false);
        var effect = (AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        var trans = AtmosphereRead(renderer, effect.TransmittanceTexture);
        var sky = AtmosphereRead(renderer, effect.SkyViewTexture);
        Assert.That(trans.All(v => float.IsFinite(v) && v >= 0 && v <= 1), Is.True);
        Assert.That(sky.All(v => float.IsFinite(v) && v >= 0), Is.True);
        double H = Math.Sqrt(AtmosphereTop * AtmosphereTop - AtmosphereR * AtmosphereR);
        foreach (int y in new[] { 0, 16, 40, 63 })
        foreach (int x in new[] { 0, 32, 128, 255 })
        {
            double h = AtmosphereReferenceAltitude(y/63.0,8), r = AtmosphereR+h;
            double rho = Math.Sqrt(h*(2*AtmosphereR+h));
            double d = (AtmosphereTop - r) + x / 255.0 * (rho + H - (AtmosphereTop - r));
            double mu = d < 1e-6 ? 1 : Math.Clamp((H * H - rho * rho - d * d) / (2 * r * d), -1, 1);
            double column = AtmosphereReferenceColumn(r, mu, d);
            double[] beta = [.0058, .0135, .0331];
            for (int c = 0; c < 3; c++)
                Assert.That(trans[4 * (y * 256 + x) + c], Is.EqualTo(Math.Exp(-beta[c] * column)).Within(.001),
                    $"transmittance ({x},{y}), channel {c}");
        }
        foreach (var (x, y) in new[] { (0, 0), (0, 64), (64, 100), (128, 120), (0, 127), (128, 127) })
        {
            double elevation = .5 * Math.PI * Math.Pow(1 - y / 127.0, 2);
            var expected = AtmosphereReferenceSky(2 * Math.PI * x / 255, elevation, altitude * Math.PI / 180);
            var longer = AtmosphereReferenceSky(2 * Math.PI * x / 255, elevation, altitude * Math.PI / 180, cutoff:20);
            for (int c = 0; c < 3; c++)
            {
                double actual = sky[4 * (y * 256 + x) + c];
                // Deliberate 10H truncation budget; absolute floor covers faint tail directions.
                Assert.That(expected[c], Is.EqualTo(longer[c]).Within(Math.Max(.0005,longer[c]*.005)), "10H versus 20H tail convergence");
                TestContext.Out.WriteLine($"sun {altitude}, sky ({x},{y}), channel {c}: {actual:G6}, reference {expected[c]:G6}");
                Assert.That(actual, Is.EqualTo(expected[c]).Within(Math.Max(.0002, expected[c] * .025)),
                    $"sky ({x},{y}), channel {c}");
            }
        }
        for (int y = 0; y < 128; y++)
        for (int c = 0; c < 3; c++)
        {
            Assert.That(sky[4 * (y * 256) + c], Is.EqualTo(sky[4 * (y * 256 + 255) + c]).Within(1e-5), "Azimuth seam");
            Assert.That(sky[4 * (y * 256 + 64) + c], Is.EqualTo(sky[4 * (y * 256 + 191) + c]).Within(1e-5), "Solar-plane symmetry");
        }
        if (altitude == 30) Assert.That(sky[2], Is.GreaterThan(sky[0]), "Blue daytime zenith");
    }

    [Test, Category("GPU")]
    public void AtmosphereCachesLutsAndSupportsDebugReloadAndZeroDensity()
    {
        using var shaders = new SceneShaders("Atmosphere");
        File.Copy(Path.Combine(ShaderDirectory, "Scenes", "Spectrum.hlsl"), Path.Combine(shaders.Path, "Scenes", "Spectrum.hlsl"));
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 1280, 720, 8, 60,
            shaderDirectory: shaders.Path, initialScene: shaders.Scene);
        var markers = new List<string>();
        renderer.TransportPassMarker = (name, begin) => { if (begin && name.StartsWith("Atmosphere ")) markers.Add(name); };
        void Draw(params string[] expected)
        {
            markers.Clear(); renderer.Render(false);
            Assert.That(markers, Is.EqualTo(expected));
        }
        Draw("Atmosphere transmittance", "Atmosphere sky view");
        var effect = (AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        var original = renderer.ReadPixels(false);
        var originalExposure = renderer.Options.Exposure;
        Assert.That(original.Skip(4 * 1280 * 650).Where((_, i) => i % 4 != 3), Is.All.Zero, "Flat black ground");
        Draw(); Assert.That(renderer.ReadPixels(false), Is.EqualTo(original), "No time or audio dependence");
        PointSet(renderer, "Phase.Strength", 0f); Draw("Atmosphere sky view");
        var rotationOriginal = renderer.ReadPixels(false);
        PointSet(renderer, "Sun.Azimuth", .25f); Draw("Atmosphere sky view");
        PointSet(renderer, "Sun2.Azimuth", .75f); Draw("Atmosphere sky view");
        PointSet(renderer, "View.Azimuth", .25f); Draw();
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(rotationOriginal).Within(1), "Rotate view and both suns together");
        foreach (float pitch in new[] { 0f, 1f }) { PointSet(renderer, "View.Altitude", pitch); Draw(); }
        Assert.That(RingReadScene(renderer).All(float.IsFinite), Is.True, "Vertical camera basis");
        PointSet(renderer, "ShowSkyView", true); Draw();
        var rawSky = renderer.ReadPixels(false);
        renderer.SetGraphicsOptions(renderer.Options with { Exposure = 8 }); Draw();
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(rawSky), "Raw sky bypasses exposure");
        PointSet(renderer, "ShowTransmittance", true); Draw();
        var rawTrans = renderer.ReadPixels(false);
        Assert.That(rawTrans, Is.Not.EqualTo(rawSky));
        PointSet(renderer, "ShowSkyView", false); Draw();
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(rawTrans), "Transmittance takes precedence");
        renderer.SetGraphicsOptions(renderer.Options with { Exposure = -8 }); Draw();
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(rawTrans), "Raw transmittance bypasses exposure");
        PointSet(renderer, "Sun.Intensity", .25f); Draw("Atmosphere sky view");
        PointSet(renderer, "Sun.Altitude", .5f); Draw("Atmosphere sky view");
        PointSet(renderer, "Sun2.Intensity", .35f); Draw("Atmosphere sky view");
        PointSet(renderer, "Sun2.Altitude", .6f); Draw("Atmosphere sky view");
        PointSet(renderer, "Phase.Anisotropy", .8f); Draw("Atmosphere sky view");
        PointSet(renderer, "Phase.Strength", .75f); Draw("Atmosphere sky view");
        PointSet(renderer, "Atmosphere.ScaleHeight", .6f); Draw("Atmosphere transmittance", "Atmosphere sky view");
        renderer.Resize(1281, 721); Draw();
        Assert.That(effect.TransmittanceTexture.Description.Width, Is.EqualTo(256));
        var path = Path.Combine(shaders.Path, "Scenes", "Atmosphere.hlsl");
        var source = File.ReadAllText(path);
        File.WriteAllText(path, "INVALID HLSL");
        Assert.That(renderer.ReloadActiveScene(), Is.False);
        Draw(); Assert.That(renderer.ActiveShader!.Effects[0], Is.SameAs(effect));
        File.WriteAllText(path, source);
        Assert.That(renderer.ReloadActiveScene(), Is.True, renderer.ShaderError);
        Draw("Atmosphere transmittance", "Atmosphere sky view");
        Assert.That(PointFind(renderer, "Sun.Intensity") is { } p && renderer.ActiveShader!.Effects[0].Parameters!.GetFloat(p) == .25f, Is.True);
        void AssertSun2Settings()
        {
            var values = renderer.ActiveShader!.Effects[0].Parameters!;
            foreach (var (path, expected) in new[] { ("Sun2.Azimuth", .75f), ("Sun2.Altitude", .6f), ("Sun2.Intensity", .35f) })
                Assert.That(values.GetFloat(PointFind(renderer, path)), Is.EqualTo(expected), path);
        }
        AssertSun2Settings();
        renderer.SelectScene("Spectrum"); renderer.Render(false);
        renderer.SelectScene("Atmosphere"); Draw("Atmosphere transmittance", "Atmosphere sky view");
        AssertSun2Settings();
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();
        renderer.SetGraphicsOptions(renderer.Options with { Exposure = originalExposure });
        renderer.Resize(1280, 720); Draw("Atmosphere transmittance", "Atmosphere sky view");
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original), "Reset restores default sky");
        foreach (string channel in new[] { "x", "y", "z" }) PointSet(renderer, "Atmosphere.RayleighScattering." + channel, 0);
        Draw("Atmosphere transmittance", "Atmosphere sky view");
        effect = (AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        Assert.That(AtmosphereRead(renderer, effect.TransmittanceTexture), Is.All.EqualTo(1));
        Assert.That(AtmosphereRead(renderer, effect.SkyViewTexture).Where((_, i) => i % 4 != 3), Is.All.Zero);
        Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero);
        // Exercise supported parameter endpoints, including optically thick and thin atmospheres.
        foreach (float value in new[] { 0f, 1f })
        {
            foreach (string channel in new[] { "x", "y", "z" }) PointSet(renderer, "Atmosphere.RayleighScattering." + channel, 1);
            PointSet(renderer, "Atmosphere.ScaleHeight", value);
            PointSet(renderer, "Sun.Altitude", value);
            PointSet(renderer, "Sun.Intensity", 1);
            Draw("Atmosphere transmittance", "Atmosphere sky view");
            Assert.That(AtmosphereRead(renderer, effect.TransmittanceTexture).All(v => float.IsFinite(v) && v >= 0 && v <= 1), Is.True);
            Assert.That(AtmosphereRead(renderer, effect.SkyViewTexture).All(v => float.IsFinite(v) && v >= 0), Is.True);
        }
    }

    [TestCase(20), TestCase(-3), Category("GPU")]
    public void AtmosphereCombinesIndependentSuns(double secondAltitude)
    {
        using var shaders = new SceneShaders("Atmosphere");
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 1280, 720, 8, 60,
            shaderDirectory: shaders.Path, initialScene: shaders.Scene);
        void Sun(string name, float azimuth, double altitude, float intensity)
        {
            PointSet(renderer, name+".Azimuth", azimuth);
            PointSet(renderer, name+".Altitude", (float)((altitude+90)/180));
            PointSet(renderer, name+".Intensity", intensity);
        }
        float[] Read()
        {
            renderer.Render(false);
            return AtmosphereRead(renderer, ((AtmosphereEffect)renderer.ActiveShader!.Effects[0]).SkyViewTexture);
        }
        void Equal(float[] actual, float[] expected, string message)
        {
            Assert.That(actual.Zip(expected, (a,b) => Math.Abs(a-b)/Math.Max(1,Math.Abs(b))).Max(), Is.LessThan(2e-6), message);
        }
        Sun("Sun", .1f, 30, .5f); Sun("Sun2", .65f, secondAltitude, 0);
        var first = Read();
        PointSet(renderer, "Sun.Intensity", 0); PointSet(renderer, "Sun2.Intensity", .25f);
        var second = Read();
        PointSet(renderer, "Sun.Intensity", .5f);
        var combined = Read();
        Equal(combined, first.Zip(second, (a,b) => a+b).Select((v,i) => i%4 == 3 ? 1 : v).ToArray(), "Linear sum of independent suns");
        foreach (var (x,y) in new[] { (0,0), (32,64), (90,110), (0,127), (180,127) })
        {
            double azimuth = 2*Math.PI*x/255, elevation = .5*Math.PI*Math.Pow(1-y/127.0,2);
            var a = AtmosphereReferenceSky(azimuth, elevation, Math.PI/6, .1*2*Math.PI);
            var b = AtmosphereReferenceSky(azimuth, elevation, secondAltitude*Math.PI/180, .65*2*Math.PI, 10);
            for (int c=0; c<3; c++)
                Assert.That(combined[4*(y*256+x)+c], Is.EqualTo(a[c]+b[c]).Within(Math.Max(.0002,(a[c]+b[c])*.025)),
                    $"Combined reference ({x},{y}), channel {c}");
        }
        for (int y=0; y<128; y++)
        for (int c=0; c<3; c++)
            Assert.That(combined[4*y*256+c], Is.EqualTo(combined[4*(y*256+255)+c]).Within(1e-5), "Combined seam");
        Sun("Sun", .65f, secondAltitude, .25f); Sun("Sun2", .1f, 30, .5f);
        Equal(Read(), combined, "Swapping suns preserves radiance");
        Sun("Sun", .1f, 30, .5f); Sun("Sun2", .65f, -90, .25f);
        Equal(Read(), first, "Planet fully occludes the second sun independently");
        Sun("Sun2", .1f, 30, .25f);
        var coincident = Read();
        PointSet(renderer, "Sun.Intensity", .75f); PointSet(renderer, "Sun2.Intensity", 0);
        Equal(Read(), coincident, "Coincident suns combine intensities");
        PointSet(renderer, "Sun.Intensity", 0);
        Assert.That(Read().Where((_,i) => i%4 != 3), Is.All.Zero, "Both suns disabled");
    }

    [Test, Category("GPU")]
    public void AtmospherePhaseMatchesFormulaAndConservesEnergy()
    {
        using var shaders = new SceneShaders("Spectrum");
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"), """
            #include "../Common/AtmosphereTypes.hlsl"
            cbuffer Parameters
            {
                AtmosphereSettings Atmosphere = { float3(.058,.135,.331), 7.0/15.0, .0625 };
                AtmosphereSunSettings Sun = { 0, 2.0/3.0, .5 };
                AtmosphereSunSettings Sun2 = { .5, 110.0/180.0, .25 };
                AtmospherePhaseSettings Phase = { .5, .5 };
                TransportSettings Transport = { false, true, false };
            };
            float AtmosphereScaleHeight() { return lerp(1.0,16.0,Atmosphere.ScaleHeight); }
            float3 AtmosphereBeta() { return .1*Atmosphere.RayleighScattering*(16*Atmosphere.Density); }
            #include "../Common/Atmosphere.hlsl"
            float4 PS(PSin input):SV_Target
            {
                float u = (input.Position.x-.5)/4096;
                float c = 1-2*(u*u)*(u*u);
                return LinearOutput(float3(AtmospherePhase(c,AtmospherePhaseCoefficients()),0,0));
            }
            """);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle,4097,1,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        foreach (var (g,strength) in new[] { (0.0,0.0), (.95,0.0), (0.0,1.0), (.8,.25), (.8,1.0), (.95,.25), (.95,1.0), (.9975,0.0), (.9975,1.0) })
        {
            PointSet(renderer,"Phase.Anisotropy",AtmosphereAnisotropySlider(g));
            PointSet(renderer,"Phase.Strength",AtmosphereStrengthSlider(strength));
            renderer.Render(false);
            var pixels = RingReadScene(renderer);
            double integral = 0;
            for (int i=0;i<=4096;i++)
            {
                float u = i/4096f;
                double c = 1f-2f*(u*u)*(u*u);
                double expected = AtmosphereReferencePhase(c,g,strength);
                double actual = pixels[4*i];
                Assert.That(actual,Is.EqualTo(expected).Within(Math.Max(1e-6,expected*.002)),$"g={g}, strength={strength}, c={c}");
                integral += (i==0 || i==4096 ? 1 : i%2==0 ? 2 : 4)*actual*(8*Math.Pow(u,3));
            }
            integral *= 2*Math.PI/4096/3;
            Assert.That(integral,Is.EqualTo(1).Within(.002),$"Normalized phase g={g}, strength={strength}");
        }
    }

    [TestCase(false), TestCase(true), Category("GPU"), Category("Performance")]
    public void AtmosphereTimingAndCapturesAt720p(bool secondEnabled)
    {
        using var shaders = new SceneShaders("Atmosphere");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        var profile = new PipelineProfile();
        string[] stages = ["atmosphere_transmittance", "atmosphere_sky_view", "scene"];
        var histories = stages.ToDictionary(s => s, s => profile.Track("gpu." + s + "_ms"));
        using var renderer = new Renderer(window.Handle, 1280, 720, 8, 60, profile: profile,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        if (!secondEnabled) PointSet(renderer, "Sun2.Intensity", 0);
        void Tick() { clock.Advance(); renderer.Render(false); renderer.ReadPixels(false); renderer.PollGpuTimings(); }
        Tick();
        // Exercise sun-only and full rebuilds separately from the subsequent cached frames.
        for (int i = 0; i < 16; i++)
        {
            PointSet(renderer, "Sun.Altitude", i % 2 == 0 ? .66f : .67f); Tick();
            PointSet(renderer, "Atmosphere.ScaleHeight", i % 2 == 0 ? .46f : .47f); Tick();
        }
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();
        if (!secondEnabled) PointSet(renderer, "Sun2.Intensity", 0);
        Tick();
        for (int i = 0; i < 180; i++) Tick();
        var report = new List<string> { $"1280x720, {renderer.AdapterDescription}, {(secondEnabled ? "two suns" : "one sun")}" };
        foreach (var stage in stages)
        {
            long cursor = 0;
            var samples = new List<TimingSample>();
            histories[stage].ReadSince(ref cursor, samples);
            Assert.That(samples.Count, Is.GreaterThan(1), stage);
            if (stage != "scene") report.Add($"initial {stage}: {samples[0].Milliseconds:F4} ms");
            // Cached display samples exclude all LUT rebuilds; update samples exclude initial construction.
            var values = (stage == "scene" ? samples.TakeLast(120) : samples.Skip(1))
                .Select(s => s.Milliseconds).Order().ToArray();
            report.Add($"{(stage == "scene" ? "cached" : "updated")} {stage}: mean {values.Average():F4} ms, p95 {values[(int)Math.Ceiling(.95 * (values.Length - 1))]:F4} ms");
        }
        var directory = Path.GetFullPath(Path.Combine(ShaderDirectory, "..", "..", "artifacts", "atmosphere"));
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, secondEnabled ? "timings-two-suns.txt" : "timings-one-sun.txt"), report);
        foreach (var line in report) TestContext.Out.WriteLine(line);
        if (!secondEnabled) return;
        void Capture(string name)
        {
            Tick();
            string path = Path.Combine(directory, name + "-1280x720.rgba");
            File.WriteAllBytes(path, renderer.ReadPixels(false)); TestContext.AddTestAttachment(path);
        }
        Capture("two-suns");
        PointSet(renderer, "Sun2.Intensity", 0); Capture("day");
        PointSet(renderer, "Sun.Altitude", .5f); Capture("sunset");
        PointSet(renderer, "Sun.Altitude", .48f); Capture("twilight");
        PointSet(renderer, "Sun.Altitude", 0f); Capture("night");
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();
        PointSet(renderer, "ShowTransmittance", true); Capture("transmittance");
        PointSet(renderer, "ShowTransmittance", false); PointSet(renderer, "ShowSkyView", true); Capture("sky-view");
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();
        PointSet(renderer, "Sun2.Intensity", 0);
        PointSet(renderer, "View.Altitude", 2f/3);
        PointSet(renderer, "Phase.Anisotropy", AtmosphereAnisotropySlider(.8));
        Capture("hg-forward-lobe");
    }
}

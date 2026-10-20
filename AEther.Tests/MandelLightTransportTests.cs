using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public class MandelLightTransportTests
{
    static string ShaderDirectory
    {
        get
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, "AEther.Vortice", "Shaders");
                if (Directory.Exists(Path.Combine(path, "Scenes"))) return path;
            }
            throw new DirectoryNotFoundException("Cannot locate shader sources.");
        }
    }

    [TestCase(false), TestCase(true), Category("GPU")]
    public void OccupiedLengthResolvesSeparatedMaterialIntervals(bool trigonometric)
    {
        using var shaders = new ShaderTree(probe: true);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 256, 1, 32, 60, shaderDirectory: shaders.Path);
        Configure(renderer);
        Set(renderer, "View.CameraDistance", 0);
        Set(renderer, "Transport.Trigonometric", trigonometric);
        renderer.Render(false);
        var pixels = renderer.ReadPixels(false);
        double error = Enumerable.Range(0, 256).Average(i => pixels[4 * i] / 255.0);
        int peak = Enumerable.Range(0, 256).Max(i => pixels[4 * i]);
        TestContext.Progress.WriteLine($"{(trigonometric ? "Trigonometric" : "Power")}: mean normalized occupied-length error {error:F5}, peak {peak / 255.0:F5}");
        Assert.That(error, Is.LessThan(.06));
        Assert.That(peak, Is.LessThan(60));
        Assert.That(Enumerable.Range(0, 256).Select(i => pixels[4 * i + 1]), Is.All.Zero,
            "Moment reconstruction must remain finite and preserve empty/full ray endpoints.");
    }

    [Test, Category("GPU")]
    public void CameraSamplesStayInTheirEpipolarSlice()
    {
        using var shaders = new ShaderTree(probe: false, epipolarProbe: true);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 96, 54, 32, 60, shaderDirectory: shaders.Path);
        Configure(renderer);
        foreach (float distance in new[] { 0f, .1f, .4324711f, 1f })
        foreach (float offset in new[] { 0f, .5f, 1f })
        foreach (float light in new[] { 0f, 1f / 3, 2f / 3, 1f })
        {
            Set(renderer, "View.CameraDistance", distance);
            Set(renderer, "View.HorizontalOffset", offset);
            Set(renderer, "View.TransportLight", light);
            renderer.Render(false);
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero,
                "Samples along a camera ray must retain their angular-map slice azimuth.");
        }
    }

    [Test, Category("GPU")]
    public void ConservativeOrbitRejectionPreservesTheDensityField()
    {
        using var shaders = new ShaderTree(probe: false, orbitProbe: true);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 96, 54, 32, 60, shaderDirectory: shaders.Path);
        foreach (float scale in new[] { 0f, .35f, .5f, .75f, 1f })
        foreach (float iterations in new[] { .2f, .46666667f, 1f })
        foreach (float potential in new[] { .2f, 1f })
        foreach (float softness in new[] { 0f, .8f })
        {
            Set(renderer, "Fractal.Scale", scale);
            Set(renderer, "Fractal.MaximumIterations", iterations);
            Set(renderer, "Fractal.Potential", potential);
            Set(renderer, "Interior.EdgeSoftness", softness);
            Set(renderer, "Transport.OrbitRejection", false);
            renderer.Render(false);
            var reference = renderer.ReadPixels(false);
            Set(renderer, "Transport.OrbitRejection", true);
            renderer.Render(false);
            Assert.That(renderer.ReadPixels(false), Is.EqualTo(reference),
                $"Density changed after orbit rejection: scale={scale}, iterations={iterations}, potential={potential}, softness={softness}.");
        }
    }

    [Test, Category("GPU")]
    public void VolumeLightingSupportsBasisChangesCacheInvalidationAndIndependentEmission()
    {
        using var shaders = new ShaderTree(probe: false);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 80, 48, 32, 60, shaderDirectory: shaders.Path);
        Configure(renderer);
        foreach (bool trigonometric in new[] { false, true })
        {
            Set(renderer, "Transport.Trigonometric", trigonometric);
            Set(renderer, "Emission.Enabled", false);
            Set(renderer, "Light.Enabled", true);
            var lit = Capture();
            Assert.That(lit.Where((_, i) => i % 4 != 3).Any(v => v > 10), Is.True);

            Set(renderer, "Transport.Trigonometric", !trigonometric);
            Capture();
            Set(renderer, "Transport.Trigonometric", trigonometric);
            Assert.That(Capture(), Is.EqualTo(lit), "Both camera and light caches must restore the selected basis.");
            Set(renderer, "Interior.EdgeSoftness", .8f);
            Assert.That(Capture(), Is.Not.EqualTo(lit));
            Set(renderer, "Interior.EdgeSoftness", .2f);
            Assert.That(Capture(), Is.EqualTo(lit));
            Set(renderer, "Air.ScaleHeight", .8f);
            Capture();
            Set(renderer, "Air.ScaleHeight", .4f);
            Assert.That(Capture(), Is.EqualTo(lit));

            Set(renderer, "View.ShowLightTransport", true);
            Set(renderer, "View.TransportLight", 1f); // A disabled light still gets a valid preview cache.
            Capture();
            Set(renderer, "View.ShowLightTransport", false);
            Assert.That(Capture(), Is.EqualTo(lit));
            Set(renderer, "View.ShowAtmosphereLookup", true);
            Capture();
            Set(renderer, "View.ShowAtmosphereLookup", false);
            Assert.That(Capture(), Is.EqualTo(lit));
            renderer.Resize(64, 40);
            Capture();
            renderer.Resize(80, 48);
            Assert.That(Capture(), Is.EqualTo(lit));
            Assert.That(renderer.ReloadShaders(), Is.True, renderer.ShaderError);
            Assert.That(Capture(), Is.EqualTo(lit));

            Set(renderer, "Light.Enabled", false);
            Set(renderer, "Emission.Enabled", true);
            Assert.That(Capture().Where((_, i) => i % 4 != 3).Any(v => v > 10), Is.True);
            Set(renderer, "Emission.Enabled", false);
            for (int frame = 0; frame < 3; frame++)
                Assert.That(Capture().Where((_, i) => i % 4 != 3), Is.All.Zero);

            // Disabling material removes it from the transport and density field.
            Set(renderer, "Light.Enabled", true);
            Set(renderer, "Interior.Enabled", false);
            var air = Capture();
            Assert.That(air.Where((_, i) => i % 4 != 3).Any(v => v > 10), Is.True);
            Set(renderer, "Interior.Enabled", true);
            Set(renderer, "Interior.Density", 0f);
            Assert.That(Capture(), Is.EqualTo(air),
                "Zero-density material must restore air scattering and transmission without a fractal cutout.");
            Set(renderer, "Interior.Density", .001f);
            var dilute = Capture();
            Assert.That(dilute.Zip(air, (a, b) => Math.Abs(a - b)).Max(), Is.LessThanOrEqualTo(1),
                "Diluting material must approach the air-only result continuously.");
            Set(renderer, "Interior.Density", .25f);
            Set(renderer, "Interior.Enabled", false);
            Set(renderer, "Fractal.Potential", .1f);
            Set(renderer, "Fractal.Scale", .2f);
            Assert.That(Capture(), Is.EqualTo(air));
            Set(renderer, "Fractal.Potential", 1f);
            Set(renderer, "Fractal.Scale", .75f);
            Set(renderer, "Interior.Enabled", true);
        }

        byte[] Capture() { renderer.Render(false); return renderer.ReadPixels(false); }
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
    static void Configure(Renderer renderer)
    {
        Set(renderer, "View.CameraSpeed", 0);
        foreach (var light in new[] { "Light", "Light2", "Light3", "Light4" }) Set(renderer, light + ".Speed", 0);
        Set(renderer, "Quality.TransportSteps", 0);
    }

    sealed class ShaderTree : IDisposable
    {
        readonly string temporaryRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        internal string Path { get; }
        internal ShaderTree(bool probe, bool orbitProbe = false, bool epipolarProbe = false)
        {
            Path = System.IO.Path.Combine(temporaryRoot, "AEther-moment-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(Path, "Scenes"));
            foreach (var file in Directory.EnumerateFiles(System.IO.Path.Combine(ShaderDirectory, "Common"), "*.hlsl", SearchOption.AllDirectories))
            {
                var target = System.IO.Path.Combine(Path, System.IO.Path.GetRelativePath(ShaderDirectory, file));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            var source = File.ReadAllText(System.IO.Path.Combine(ShaderDirectory, "Scenes", "MandelLight.hlsl"));
            if (probe)
            {
                source = Regex.Replace(source, @"float InteriorMask\(float3 p\)\s*\{.*?\}", """
                    float InteriorMask(float3 p)
                    {
                        float r=length(p);
                        return HasInterior() && ((r>1 && r<2) || (r>4 && r<5)) ? 1 : 0;
                    }
                    """, RegexOptions.Singleline);
                source = source.Replace("float4 PS(PSin input)", "float4 OriginalPS(PSin input)") + """

                    float4 PS(PSin input) : SV_Target
                    {
                        float r=12*input.UV.x;
                        float2 medium=AccumulatedMedium(ReadCameraTransport((int2)input.Position.xy),r,0,InteriorRadius);
                        float exact=clamp(r-1,0,1)+clamp(r-4,0,1);
                        bool invalid=!all(isfinite(medium)) || medium.x<0 || medium.x>2.1;
                        invalid=invalid || (r>=InteriorRadius && abs(medium.x-2)>.1);
                        // Keep the atmosphere pass in the graph, as in the scene.
                        invalid=invalid || AtmosphereOpticalDepth.Load(int3(0,0,0))<0;
                        return float4(abs(medium.x-exact)/2,invalid ? 1 : 0,0,1);
                    }
                    """;
            }
            if (orbitProbe)
                source = source.Replace("float4 PS(PSin input)", "float4 OriginalPS(PSin input)") + """

                    float4 PS(PSin input) : SV_Target
                    {
                        float z=2*frac(dot(input.Position.xy,float2(.61803399,.41421356)))-1;
                        float3 p=float3(2*input.UV-1,z)*InteriorRadius;
                        return float4(InteriorMask(p).xxx,1);
                    }
                    """;
            if (epipolarProbe)
                source = source.Replace("float4 PS(PSin input)", "float4 OriginalPS(PSin input)") + """

                    float4 PS(PSin input) : SV_Target
                    {
                        LightContext source=MakeLightContext(PreviewLight());
                        float3 direction=CameraRay(input.UV);
                        float2 plane=float2(dot(direction,source.U),dot(direction,source.V));
                        if (dot(plane,plane)<1e-8) return float4(0,0,0,1);
                        float expected=atan2(plane.y,plane.x);
                        float error=0;
                        [unroll] for (int i=0;i<3;++i)
                        {
                            float t=i==0 ? .25 : (i==1 ? 3 : 25);
                            float3 delta=CameraPosition()+t*direction-source.Position;
                            float angle=atan2(dot(delta,source.V),dot(delta,source.U));
                            float wrapped=frac((angle-expected)/(2*Pi)+.5)-.5;
                            error=max(error,abs(wrapped)>1e-3 ? 1 : 0);
                        }
                        return float4(error.xxx,1);
                    }
                    """;
            File.WriteAllText(System.IO.Path.Combine(Path, "Scenes", "MandelLight.hlsl"), source);
        }
        public void Dispose()
        {
            if (!System.IO.Path.GetFullPath(Path).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Shader cleanup escaped the temporary directory.");
            Directory.Delete(Path, true);
        }
    }
    sealed class HiddenWindow : IDisposable
    {
        internal IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "AEther volume transport regression",
            0x80000000, 0, 0, 256, 48, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        internal HiddenWindow()
        {
            if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        }
        public void Dispose() => DestroyWindow(Handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint styleEx, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr handle);
    }
}

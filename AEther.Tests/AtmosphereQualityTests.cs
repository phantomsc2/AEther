using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [TestCase("Planet"),TestCase("Atmosphere"),Category("GPU"),Category("Performance")]
    public void AtmosphereQualityTradeoffsAt720p(string scene)
    {
        var report=new List<string>();
        var images=new Dictionary<int,float[][]>();
        foreach(int steps in new[]{64,128,256})
        {
            using var shaders=new SceneShaders(scene);
            string path=Path.Combine(shaders.Path,"Common","Atmosphere.hlsl");
            string source=File.ReadAllText(path);
            Assert.That(source,Does.Contain("AtmosphereTransmittanceSteps = 128, AtmosphereSkySteps = 64"));
            source=source.Replace("AtmosphereTransmittanceSteps = 128, AtmosphereSkySteps = 64",
                $"AtmosphereTransmittanceSteps = {steps*2}, AtmosphereSkySteps = {steps}");
            File.WriteAllText(path,source);
            using var window=new HiddenWindow();
            var profile=new PipelineProfile();var clock=new RenderClock();
            var trans=profile.Track("gpu.atmosphere_transmittance_ms");
            var sky=profile.Track("gpu.atmosphere_sky_view_ms");
            using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,
                initialScene:scene,profile:profile,timeProvider:clock);
            void Tick(){clock.Advance();renderer.Render(false);renderer.ReadPixels(false);renderer.PollGpuTimings();}
            // Same work and warm-up for each configuration; force both LUTs to rebuild.
            for(int frame=0;frame<48;frame++)
            {
                float density=scene=="Planet" ? .15f : .0625f;
                PointSet(renderer,"Atmosphere.Density",density+(frame%2==0 ? 0 : .001f));Tick();
            }
            foreach(var (name,history) in new[]{("transmittance",trans),("skyview",sky)})
            {
                long cursor=0;var samples=new List<TimingSample>();history.ReadSince(ref cursor,samples);
                var values=samples.Skip(8).Select(s=>s.Milliseconds).Order().ToArray();
                Assert.That(values.Length,Is.GreaterThanOrEqualTo(32));
                report.Add($"{scene} {steps} sky / {steps*2} trans steps, {name}: {values.Average():F4} ms mean, "+
                    $"{values[(int)(.95*(values.Length-1))]:F4} ms p95, {renderer.AdapterDescription}, 1280x720");
            }
            renderer.ActiveShader!.Effects[0].Parameters!.Reset();Tick();
            var day=RingReadScene(renderer);
            if(scene=="Planet")
            {
                string captures=Path.GetFullPath(Path.Combine(ShaderDirectory,"..","..","artifacts","planet"));
                Directory.CreateDirectory(captures);
                renderer.SetGraphicsOptions(renderer.Options with { Exposure=2.616 });Tick();
                File.WriteAllBytes(Path.Combine(captures,$"shadow-{steps}-steps-1280x720.rgba"),renderer.ReadPixels());
                renderer.SetGraphicsOptions(renderer.Options with { Exposure=0 });
            }
            if(scene=="Planet")
            {
                foreach(var (key,value) in new[]{("Camera.Alt",PlanetReferenceOrbit(0)),("Camera.Long",.618f),("Camera.Lat",.359f),
                    ("Sun.Altitude",.233f),("Sun.Azimuth",.294f),("Sun.Intensity",.484f),("Sun2.Intensity",0f),
                    ("Atmosphere.Phase.Anisotropy",AtmosphereAnisotropySlider(.95*.580)),("Atmosphere.Phase.Strength",AtmosphereStrengthSlider(.25))}) PointSet(renderer,key,value);
            }
            else {PointSet(renderer,"Sun.Altitude",.48f);PointSet(renderer,"Sun2.Intensity",0);}
            Tick();images[steps]=[day,RingReadScene(renderer)];
        }
        foreach(int steps in new[]{64,128})
        for(int preset=0;preset<2;preset++)
        {
            var actual=images[steps][preset];var reference=images[256][preset];
            double error=0,energy=0;
            for(int i=0;i<actual.Length;i++) if(i%4!=3)
            {error+=Math.Pow(actual[i]-reference[i],2);energy+=reference[i]*reference[i];}
            double rms=Math.Sqrt(error/Math.Max(energy,1e-20));
            report.Add($"{scene} {steps} steps, preset {preset}: relative image RMS error {rms:P4} versus 256 steps");
            Assert.That(rms,Is.LessThan(.03));
        }
        string directory=Path.GetFullPath(Path.Combine(ShaderDirectory,"..","..","artifacts","atmosphere"));
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory,$"quality-{scene.ToLowerInvariant()}-720p.txt"),report);
        foreach(string line in report) TestContext.Out.WriteLine(line);
    }
}

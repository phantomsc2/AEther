#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;
public partial class ShaderRenderingTests
{
    [Test, Category("GPU"), Category("Performance")]
    public void PlanetOptimizationABAt3200x2000()
    {
        string output=Path.GetFullPath(Path.Combine(ShaderDirectory,"..","..","artifacts","planet-ab"));
        Directory.CreateDirectory(output);
        var report=new List<string>{"3200x2000; fixed default camera; frozen shell; camera experiment uses compiler-baked constants."};
        float[]? reference=null;
        foreach(string variant in new[]{"baseline","camera","half-sky","baseline-repeat"})
        {
            using var shaders=new SceneShaders("Planet");
            if(variant=="camera") {
                string path=Path.Combine(shaders.Path,"Scenes","Planet.hlsl");
                string source=File.ReadAllText(path);
                // Specialize only function bodies, preserving the public parameter layout.
                int start=source.IndexOf("float3 PlanetObserver()",StringComparison.Ordinal);
                string body=source[start..];
                foreach(var pair in new[]{("Alt",".1024908"),("Long","0.0"),("Lat",".5"),("Yaw",".5"),("Pitch",".5"),("Fov",".81818182")})
                    body=body.Replace("Camera."+pair.Item1,"("+pair.Item2+")");
                body=body.Replace("Atmosphere.ScaleHeight","(.18)");
                File.WriteAllText(path,source[..start]+body);
            }
            using var window=new HiddenWindow();
            var profile=new PipelineProfile();
            using var renderer=new Renderer(window.Handle,3200,2000,8,60,shaderDirectory:shaders.Path,profile:profile,initialScene:shaders.Scene);
            Assert.That(renderer.ShaderError,Is.Null);
            var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
            if(variant=="half-sky") effect.ExperimentalHalfSky=true;
            PointSet(renderer,"Shell.Speed",0);
            report.Add(variant+": "+renderer.AdapterDescription);
            var history=profile.Track("gpu.scene_ms");
            long cursor=0;
            for(int i=0;i<32;i++) { renderer.Render(false); renderer.ReadPixels(false); renderer.PollGpuTimings(); }
            history.ReadSince(ref cursor,new List<TimingSample>());
            for(int i=0;i<120;i++) { renderer.Render(false); renderer.ReadPixels(false); renderer.PollGpuTimings(); }
            var samples=new List<TimingSample>(); history.ReadSince(ref cursor,samples);
            var times=samples.Select(s=>s.Milliseconds).Order().ToArray();
            Assert.That(times.Length,Is.GreaterThan(60));
            report.Add($"cached scene: n={times.Length}, mean={times.Average():F4} ms, median={times[times.Length/2]:F4}, p95={times[(int)(.95*(times.Length-1))]:F4}");
            var image=RingReadScene(renderer);
            if(reference==null) reference=image;
            double error=0,energy=0,max=0;
            for(int i=0;i<image.Length;i++) if(i%4!=3) { double d=image[i]-reference[i]; error+=d*d; energy+=reference[i]*reference[i]; max=Math.Max(max,Math.Abs(d)); }
            report.Add($"linear RGB relative RMS={Math.Sqrt(error/Math.Max(energy,1e-20)):P6}; max absolute={max:G6}");
            File.WriteAllBytes(Path.Combine(output,variant+".rgba"),renderer.ReadPixels(false));
        }
        File.WriteAllLines(Path.Combine(output,"results.txt"),report);
        foreach(string line in report) TestContext.Out.WriteLine(line);
    }
}

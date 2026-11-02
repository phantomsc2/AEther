using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;
public partial class ShaderRenderingTests
{
    [Test, Category("GPU"), Category("Performance")]
    public void PlanetPhaseSeedPerformanceABAt1080p()
    {
        using var shaders=new SceneShaders("Planet",multipleScattering:true);
        using var window=new HiddenWindow();
        var profile=new PipelineProfile();
        using var renderer=new Renderer(window.Handle,1920,1080,8,60,shaderDirectory:shaders.Path,
            initialScene:shaders.Scene,timeProvider:new FroxelClock(),profile:profile);
        var stages=new[]{"frame","scene","atmosphere_transmittance","atmosphere_multiple_scattering","atmosphere_sky_view","atmosphere_aerial_perspective"};
        var histories=stages.ToDictionary(s=>s,s=>profile.Track("gpu."+s+"_ms"));
        var cursors=stages.ToDictionary(s=>s,s=>0L);
        var all=new Dictionary<string,List<double>>();
        var report=new List<string>{"Planet, 1920x1080, "+renderer.AdapterDescription,
            "Default scene controls, L1 and L2+ enabled, frozen clock. GPU timestamp timings; present disabled; readback outside timed frame.",
            "Per scenario: OFF, ON, ON, OFF; each block has 24 warmup frames and 96 measured frames.",
            "phase-edit alternates Phase.Strength .5/.501; density-edit alternates Atmosphere.Density .5/.501."};
        var csv=new List<string>{"scenario,block,enabled,stage,milliseconds"};
        var markers=new List<string>();renderer.TransportPassMarker=(s,b)=>{if(b)markers.Add(s);};
        void Tick() { markers.Clear();renderer.Render(false);renderer.ReadPixels(false);renderer.PollGpuTimings();Assert.That(renderer.ShaderError,Is.Null); }
        foreach(string scenario in new[]{"cached","phase-edit","density-edit"})
        {
            for(int block=0;block<4;block++)
            {
                bool enabled=block==1 || block==2;
                PointSet(renderer,"Atmosphere.Phase.UseForMultipleScattering",enabled);
                PointSet(renderer,"Atmosphere.Phase.Strength",.5f);
                PointSet(renderer,"Atmosphere.Density",.5f);
                void Step(int i) {
                    if(scenario=="phase-edit") PointSet(renderer,"Atmosphere.Phase.Strength",i%2==0?.501f:.5f);
                    if(scenario=="density-edit") PointSet(renderer,"Atmosphere.Density",i%2==0?.501f:.5f);
                    Tick();
                }
                for(int i=0;i<24;i++) Step(i);
                foreach(string stage in stages) {
                    long cursor=cursors[stage];histories[stage].ReadSince(ref cursor,new List<TimingSample>());cursors[stage]=cursor;
                }
                for(int i=0;i<96;i++) {
                    Step(i);
                    bool rebuild=scenario=="density-edit" || (scenario=="phase-edit" && enabled);
                    Assert.That(markers.Contains("Atmosphere multiple scattering"),Is.EqualTo(rebuild));
                }
                foreach(string stage in stages) {
                    long cursor=cursors[stage];var samples=new List<TimingSample>();histories[stage].ReadSince(ref cursor,samples);cursors[stage]=cursor;
                    if(stage=="frame") Assert.That(samples.Count,Is.EqualTo(96));
                    string key=scenario+" "+enabled+" "+stage;
                    if(!all.ContainsKey(key))all[key]=new List<double>();
                    all[key].AddRange(samples.Select(s=>s.Milliseconds));
                    foreach(var sample in samples) csv.Add($"{scenario},{block},{enabled},{stage},{sample.Milliseconds.ToString("R",CultureInfo.InvariantCulture)}");
                    if(samples.Count>0) report.Add($"{scenario} block {block} {(enabled?"ON":"OFF")} {stage}: n={samples.Count}, mean={samples.Average(s=>s.Milliseconds):F5} ms");
                }
            }
        }
        report.Add("POOLED RESULTS (192 measured frames per toggle/scenario):");
        foreach(var (key,values) in all.Where(kv=>kv.Value.Count>0)) {
            var sorted=values.Order().ToArray();
            report.Add($"{key}: n={sorted.Length}, mean={sorted.Average():F5}, median={sorted[sorted.Length/2]:F5}, p95={sorted[(int)(.95*(sorted.Length-1))]:F5} ms");
        }
        report.Add($"Skipped timing frames={renderer.SkippedGpuTimingFrames}; invalid timing frames={renderer.InvalidGpuTimingFrames}");
        string directory=Path.GetFullPath(Path.Combine(ShaderDirectory,"..","..","artifacts","phase-seed-1080p"));
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory,"results.txt"),report);
        File.WriteAllLines(Path.Combine(directory,"samples.csv"),csv);
        foreach(string line in report)TestContext.Out.WriteLine(line);
    }
}

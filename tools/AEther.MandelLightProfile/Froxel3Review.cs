using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunFroxel3Review(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/froxel3-review");
        string variant=Path.Combine(output,"Shaders");Directory.CreateDirectory(variant);
        foreach(string file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
        {
            string target=Path.Combine(variant,Path.GetRelativePath(source,file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);
        }
        string scenePath=Path.Combine(variant,"Scenes","Froxel3.hlsl"), ringPath=Path.Combine(variant,"Common","UniformRing.hlsl");
        string scene=File.ReadAllText(scenePath),ring=File.ReadAllText(ringPath);
        var scenarios=new[] {
            (Name:"default",Options:new Froxel3Options(),G:.8f),
            (Name:"depth128",Options:new Froxel3Options(128,4096),G:.8f),
            (Name:"depth64",Options:new Froxel3Options(64,4096),G:.8f),
            (Name:"no_objects",Options:new Froxel3Options(256,0),G:.8f),
            (Name:"isotropic",Options:new Froxel3Options(),G:.5f),
            (Name:"constant_medium",Options:new Froxel3Options(),G:.8f),
            (Name:"one_ring",Options:new Froxel3Options(),G:.8f),
            (Name:"one_phase_direction",Options:new Froxel3Options(),G:.8f),
            (Name:"no_fog_lighting",Options:new Froxel3Options(),G:.8f),
            (Name:"point_sampled_reference",Options:new Froxel3Options(),G:.8f),
            (Name:"default_repeat",Options:new Froxel3Options(),G:.8f),
        };
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var results=new List<object>();string adapter="";
        foreach(var scenario in scenarios.Where(s=>!args.Contains("--baseline-only") || s.Name.StartsWith("default")))
        {
            string current=scene,currentRing=ring;
            if(scenario.Name=="constant_medium")
            {
                int a=current.IndexOf("    float noise = VolumeNoise"),b=current.IndexOf("    FroxelMedium m;",a);
                current=current.Remove(a,b-a).Insert(a,"    float noise=0;\n");
            }
            if(scenario.Name=="one_ring") current=current.Replace("i<3;++i)","i<1;++i)");
            if(scenario.Name=="no_fog_lighting")
            {
                int a=current.IndexOf("float3 EvaluateFroxelSegment("),b=current.IndexOf("float3 EvaluateFroxelSurface(",a);
                current=current.Remove(a,b-a).Insert(a,"float3 EvaluateFroxelSegment(float3 begin,float3 end,float3 scattering) { return 0; }\n");
            }
            if(scenario.Name=="point_sampled_reference") current=current.Replace("#define FROXEL_SEGMENT_SOURCE","// point-sampled reference");
            if(scenario.Name=="one_phase_direction")
                currentRing=currentRing.Replace("[unroll] for(uint i=0;i<8;++i)","[loop] for(uint i=0;i<(phase?1:8);++i)")
                    .Replace("float h=k*tangents[i]","float h=phase?0:k*tangents[i]").Replace("return sum/8;","return sum/(phase?1:8);");
            File.WriteAllText(scenePath,current);File.WriteAllText(ringPath,currentRing);
            using var renderer=new Renderer(window.Handle,1280,720,128,60,shaderDirectory:variant,timeProvider:new Clock(),initialScene:"Froxel3");
            renderer.SetGraphicsOptions(renderer.Options with {Exposure=-1.488});
            adapter=renderer.AdapterDescription;
            var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];effect.CloudOptions=scenario.Options;
            Set(renderer,"Medium.Anisotropy",scenario.G);
            var device=Field<ID3D11Device>(renderer,"device");var context=Field<ID3D11DeviceContext>(renderer,"context");
            for(int i=0;i<64;i++) {renderer.Render(false);renderer.ReadPixels(false);}
            using var timer=new Timestamps(device,context);var frames=new List<Dictionary<string,double>>();
            for(int i=0;i<31;i++)
            {
                timer.Reset();renderer.TransportPassMarker=timer.Marker;
                timer.Begin();renderer.Render(false);timer.End();frames.Add(timer.Resolve());renderer.TransportPassMarker=null;
            }
            var medians=frames.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(v=>v.GetValueOrDefault(k))));
            Console.WriteLine(scenario.Name+": "+string.Join(", ",medians.Select(v=>$"{v.Key}={v.Value:F3}ms")));
            results.Add(new {name=scenario.Name,options=scenario.Options,g=2*scenario.G-1,width=1280,height=720,milliseconds=medians});
            Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,scenario.Name+".png"));
        }
        File.WriteAllText(scenePath,scene);File.WriteAllText(ringPath,ring);
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(new {
            adapter,note="720p, fixed time, exposure -1.488, 64 warmup frames and 31 timestamp samples per scenario. Ablations alter rendering and isolate cost; they are not equivalent-quality optimizations. No concurrent test workload. Total excludes Present/readback.",results
        },new JsonSerializerOptions {WriteIndented=true}));
    }
}

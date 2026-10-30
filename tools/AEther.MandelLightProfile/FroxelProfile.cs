using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunFroxelProfile(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/froxel1");
        Directory.CreateDirectory(output);
        using var window=new Form { ClientSize=new(1280,720),ShowInTaskbar=false };
        var clock=new Clock();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:source,timeProvider:clock,initialScene:"Froxel1");
        var device=Field<ID3D11Device>(renderer,"device");
        var context=Field<ID3D11DeviceContext>(renderer,"context");
        var effect=(FroxelEffect)renderer.ActiveShader!.Effects[0];
        renderer.Render(false);
        Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"froxel1-default.png"));
        for(int i=0;i<180;i++) clock.Advance(); renderer.Render(false);
        Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"froxel1-animated.png"));
        Set(renderer,"Camera.Distance",.12f); Set(renderer,"Camera.Yaw",.65f); renderer.Render(false);
        Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"froxel1-inside.png"));
        effect.Parameters!.Reset();
        // Fixed scene time for comparable material samples across all workloads.
        var results=new List<object>();
        foreach(int slices in new[] {16,64,128}) foreach(int count in new[] {1,4,16,64})
        {
            effect.Options=new(slices,count);
            for(int i=0;i<16;i++) { renderer.Render(false); renderer.ReadPixels(false); }
            using var timer=new Timestamps(device,context);
            var samples=new List<Dictionary<string,double>>();
            for(int i=0;i<31;i++)
            {
                renderer.TransportPassMarker=null; renderer.Render(false);
                timer.Reset(); renderer.TransportPassMarker=timer.Marker;
                timer.Begin(); renderer.Render(false); timer.End(); samples.Add(timer.Resolve());
            }
            renderer.TransportPassMarker=null;
            var medians=samples.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(samples.Select(s=>s.GetValueOrDefault(k))));
            Console.WriteLine($"{slices} slices, {count} lights: "+string.Join(", ",medians.Select(v=>$"{v.Key}={v.Value:F3}ms")));
            results.Add(new {width=1280,height=720,slices,lights=count,milliseconds=medians});
            if(count==4) Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,$"froxel1-{slices}.png"));
        }
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(new
        {
            adapter=renderer.AdapterDescription,
            note="1280x720, 16 warmup frames, median of 31 GPU timestamp samples; Total includes presentation shader but no readback or Present. No audio. Stable spatial jitter, no temporal history.",
            results,
        },new JsonSerializerOptions {WriteIndented=true}));
    }
}

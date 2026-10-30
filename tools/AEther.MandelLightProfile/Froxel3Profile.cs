using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunFroxel3Profile(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/froxel3-uniform");
        Directory.CreateDirectory(output);
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var results=new List<object>();string adapter="";
        foreach(int bands in new[] {128})
        {
            var clock=new Clock();
            using var renderer=new Renderer(window.Handle,1280,720,bands,60,shaderDirectory:source,timeProvider:clock,initialScene:"Froxel3");
            adapter=renderer.AdapterDescription;
            var device=Field<ID3D11Device>(renderer,"device");var context=Field<ID3D11DeviceContext>(renderer,"context");
            var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];
            void Render()
            {
renderer.Render(false);
            }
            var configurations=new List<Froxel3Options> {new()};
            if(bands==128)
            {
                configurations.AddRange(new[] {32,128}.Select(n=>new Froxel3Options(DepthSlices:n)));
                configurations.AddRange(new[] {0,4096}.Select(n=>new Froxel3Options(Instances:n)));
            }
            var workloads=args.Contains("--phase")
                ? new[] {.2f,.5f,.8f,.95f}.Select(g=>(Options:new Froxel3Options(),Anisotropy:g))
                : configurations.Select(o=>(Options:o,Anisotropy:.8f));
            foreach(var workload in workloads)
            {
                var options=workload.Options;
                Set(renderer,"Medium.Anisotropy",workload.Anisotropy);
                effect.CloudOptions=options;
                for(int i=0;i<8;i++) {Render();renderer.ReadPixels(false);}
                using var timer=new Timestamps(device,context);var frames=new List<Dictionary<string,double>>();
                for(int i=0;i<21;i++)
                {
                    timer.Reset();renderer.TransportPassMarker=timer.Marker;
                    timer.Begin();Render();timer.End();frames.Add(timer.Resolve());renderer.TransportPassMarker=null;
                }
                var medians=frames.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(v=>v.GetValueOrDefault(k))));
                float g=2*workload.Anisotropy-1;
                Console.WriteLine($"g={g:F2}, {options}: "+string.Join(", ",medians.Select(v=>$"{v.Key}={v.Value:F3}ms")));
                results.Add(new {bands,options,g,width=1280,height=720,milliseconds=medians});
                string phaseSuffix=args.Contains("--phase") ? FormattableString.Invariant($"-g{g:F2}") : "";
                Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,$"ring-{bands}-depth{options.DepthSlices}-instances{options.Instances}{phaseSuffix}.png"));
            }
            if(bands==128)
            {
                effect.CloudOptions=new();
                Set(renderer,"Medium.Anisotropy",.8f);
                for(int i=0;i<180;i++) clock.Advance();Render();
                Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"animated.png"));
                Set(renderer,"Medium.Density",0);Render();
                Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"surfaces.png"));
                Set(renderer,"Medium.Density",.159f);Set(renderer,"Camera.Distance",.12f);Set(renderer,"Camera.Yaw",.65f);Render();
                Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"inside.png"));
            }
        }
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(new {
            adapter,note="1280x720; 8 warmup frames, median of 21 GPU timestamp samples. Total includes audio upload and output, excludes Present/readback. Fixed scene time for timing; one axis varied at a time.",results
        },new JsonSerializerOptions {WriteIndented=true}));
    }
}

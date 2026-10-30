using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunFroxel3Profile(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/froxel3");
        Directory.CreateDirectory(output);
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var results=new List<object>();string adapter="";
        foreach(int bands in new[] {16,128,512})
        {
            var clock=new Clock();
            using var renderer=new Renderer(window.Handle,1280,720,bands,60,shaderDirectory:source,timeProvider:clock,initialScene:"Froxel3");
            adapter=renderer.AdapterDescription;
            var device=Field<ID3D11Device>(renderer,"device");var context=Field<ID3D11DeviceContext>(renderer,"context");
            var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];long row=0;
            void Render()
            {
                var samples=new double[8*bands];
                for(int side=0;side<2;side++) for(int bin=0;bin<bands;bin++) for(int c=0;c<3;c++)
                {
                    double f=(bin+.5)/bands;
                    samples[4*(side*bands+bin)+c]=.002+.045*Math.Pow(.5+.5*Math.Cos(18*f+side*1.7+c*2.094),12);
                }
                renderer.PostAudio(new(samples,4*bands,DateTime.UnixEpoch) {AnalysisRow=++row},2);renderer.Render(false);
            }
            var configurations=new List<Froxel3Options> {new()};
            if(bands==128)
            {
                configurations.AddRange(new[] {32,128}.Select(n=>new Froxel3Options(DepthSlices:n)));
                configurations.AddRange(new[] {4,16,32}.Select(n=>new Froxel3Options(LightingArcs:n)));
                configurations.AddRange(new[] {0,4096}.Select(n=>new Froxel3Options(Instances:n)));
            }
            foreach(var options in configurations)
            {
                effect.CloudOptions=options;
                for(int i=0;i<8;i++) {Render();renderer.ReadPixels(false);}
                using var timer=new Timestamps(device,context);var frames=new List<Dictionary<string,double>>();
                for(int i=0;i<21;i++)
                {
                    timer.Reset();renderer.TransportPassMarker=timer.Marker;
                    timer.Begin();Render();timer.End();frames.Add(timer.Resolve());renderer.TransportPassMarker=null;
                }
                var medians=frames.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(v=>v.GetValueOrDefault(k))));
                Console.WriteLine($"{bands} bands, {options}: "+string.Join(", ",medians.Select(v=>$"{v.Key}={v.Value:F3}ms")));
                results.Add(new {bands,options,width=1280,height=720,milliseconds=medians});
                Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,$"ring-{bands}-depth{options.DepthSlices}-arcs{options.LightingArcs}-instances{options.Instances}.png"));
            }
            if(bands==128)
            {
                effect.CloudOptions=new();
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

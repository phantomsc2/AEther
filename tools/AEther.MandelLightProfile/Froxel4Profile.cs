using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunFroxel4Profile(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/froxel4");Directory.CreateDirectory(output);
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var results=new List<object>();string adapter="";
        foreach(int bands in new[] {4,16,48})
        {
            var clock=new Clock();
            using var renderer=new Renderer(window.Handle,1280,720,bands,60,shaderDirectory:source,timeProvider:clock,initialScene:"Froxel4");
            adapter=renderer.AdapterDescription;
            renderer.SetGraphicsOptions(renderer.Options with {Exposure=-1.488});
            var effect=(Froxel4Effect)renderer.ActiveShader!.Effects[0];
            var device=Field<ID3D11Device>(renderer,"device");var context=Field<ID3D11DeviceContext>(renderer,"context");long row=0;
            void Render()
            {
                var samples=new double[8*bands];
                for(int side=0;side<2;side++) for(int bin=0;bin<bands;bin++) for(int c=0;c<3;c++)
                {
                    double f=(bin+.5)/bands;
                    samples[4*(side*bands+bin)+c]=.003+.04*Math.Pow(.5+.5*Math.Cos(12*f+side*1.7+c*2.094),8);
                }
                renderer.PostAudio(new(samples,4*bands,DateTime.UnixEpoch) {AnalysisRow=++row},2);renderer.Render(false);
            }
            var workloads=new List<(int Slices,int Instances)> {(256,4096)};
            if(bands==16) workloads.AddRange(new[] {(64,4096),(128,4096),(256,0)});
            foreach(var w in workloads)
            {
                effect.CloudOptions=new(w.Slices,w.Instances);
                for(int i=0;i<8;i++) {Render();renderer.ReadPixels(false);}
                using var timer=new Timestamps(device,context);var frames=new List<Dictionary<string,double>>();
                for(int i=0;i<11;i++)
                {
                    timer.Reset();renderer.TransportPassMarker=timer.Marker;
                    timer.Begin();Render();timer.End();frames.Add(timer.Resolve());renderer.TransportPassMarker=null;
                }
                var medians=frames.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(v=>v.GetValueOrDefault(k))));
                Console.WriteLine($"{2*bands} lines, {w.Slices} slices, {w.Instances} objects, isotropic: "+string.Join(", ",medians.Select(v=>$"{v.Key}={v.Value:F3}ms")));
                results.Add(new {bands,lines=2*bands,slices=w.Slices,instances=w.Instances,g=0,width=1280,height=720,milliseconds=medians});
                Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,FormattableString.Invariant($"tube-{2*bands}-depth{w.Slices}-objects{w.Instances}-isotropic.png")));
            }
            if(bands==16)
            {
                effect.CloudOptions=new(256,4096);
                for(int i=0;i<180;i++) clock.Advance();Render();Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"animated.png"));
                Set(renderer,"Medium.Density",0);Render();Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"surfaces.png"));
                Set(renderer,"Medium.Density",.36f);Set(renderer,"Camera.Distance",.12f);Render();Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,"inside.png"));
            }
        }
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(new {
            adapter,note="1280x720, exposure -1.488, 8 warmup frames, medians of 11 GPU timestamp samples. All lines active, no selection/culling; Total excludes Present/readback. Clock variation affects comparisons.",results
        },new JsonSerializerOptions {WriteIndented=true}));
    }
}

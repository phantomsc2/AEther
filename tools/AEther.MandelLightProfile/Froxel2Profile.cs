using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunFroxel2Profile(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/froxel2");
        Directory.CreateDirectory(output);
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var results=new List<object>();string adapter="";
        foreach(int bands in new[] {16,64,128})
        {
            var clock=new Clock();
            using var renderer=new Renderer(window.Handle,1280,720,bands,60,shaderDirectory:source,timeProvider:clock,initialScene:"Froxel2");
            adapter=renderer.AdapterDescription;
            var device=Field<ID3D11Device>(renderer,"device");var context=Field<ID3D11DeviceContext>(renderer,"context");
            var effect=(Froxel2Effect)renderer.ActiveShader!.Effects[0];long row=0;
            void Render()
            {
                var samples=new double[8*bands];
                for(int channel=0;channel<2;channel++) for(int bin=0;bin<bands;bin++)
                {
                    double f=(bin+.5)/bands;
                    for(int c=0;c<3;c++) samples[4*(channel*bands+bin)+c]=.002+.045*Math.Pow(.5+.5*Math.Cos(18*f+channel*1.7+c*2.094),12);
                }
                renderer.PostAudio(new(samples,4*bands,DateTime.UnixEpoch) {AnalysisRow=++row},2);renderer.Render(false);
            }
            foreach(int slices in new[] {32,86,128})
            {
                effect.DepthOptions=new(slices);
                for(int i=0;i<5;i++) {Render();renderer.ReadPixels(false);}
                using var timer=new Timestamps(device,context);var frames=new List<Dictionary<string,double>>();
                for(int i=0;i<11;i++)
                {
                    timer.Reset();renderer.TransportPassMarker=timer.Marker;
                    timer.Begin();Render();timer.End();frames.Add(timer.Resolve());renderer.TransportPassMarker=null;
                }
                var medians=frames.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(v=>v.GetValueOrDefault(k))));
                Console.WriteLine($"{bands} bands/channel, {slices} slices: "+string.Join(", ",medians.Select(v=>$"{v.Key}={v.Value:F3}ms")));
                results.Add(new {bands,slices,width=1280,height=720,milliseconds=medians});
                if(slices==86)
                {
                    Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,$"ring-{bands}.png"));
                }
            }
            effect.DepthOptions=new(86);
            for(int i=0;i<3;i++) clock.Advance();
            Render();Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,$"ring-{bands}-jitter.png"));
        }
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(new {
            adapter,note="1280x720, 5 warmup frames, median of 11 GPU timestamp samples. Continuous ring with exact softened arc integrals; 2 arcs per spectrum band. Total includes audio upload and output, excludes Present/readback. Reference path has no lighting cache.",results
        },new JsonSerializerOptions {WriteIndented=true}));
    }
}

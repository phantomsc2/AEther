using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunFroxel4InfiniteCompare(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/froxel4-infinite-production");
        string legacy=Path.GetFullPath(positional.ElementAtOrDefault(2) ?? "artifacts/froxel4-finite-reference/Shaders");
        if(!File.Exists(Path.Combine(legacy,"Scenes","Froxel4.hlsl")))
            throw new InvalidOperationException("Provide saved finite-line shaders as the third positional argument.");
        string benchmark=Path.GetFullPath(positional.ElementAtOrDefault(3) ?? "artifacts/froxel4-infinite/Shaders");
        if(!File.Exists(Path.Combine(benchmark,"Scenes","Froxel4.hlsl"))) throw new InvalidOperationException("Provide the benchmarked infinite-line shader directory as the fourth positional argument.");
        Directory.CreateDirectory(output);
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var results=new List<object>();var comparisons=new List<object>();string adapter="";
        foreach(int slices in new[] {64,256})
        {
            var isotropicImages=new Dictionary<string,byte[][]>();
            foreach(string mode in new[] {"finite","benchmark","production","production_repeat"})
            {
                string shaders=mode=="finite" ? legacy : mode=="benchmark" ? benchmark : source;
                using var renderer=new Renderer(window.Handle,1280,720,16,60,shaderDirectory:shaders,timeProvider:new Clock(),initialScene:"Froxel4");
                adapter=renderer.AdapterDescription;renderer.SetGraphicsOptions(renderer.Options with {Exposure=-1.488});
                var effect=(Froxel4Effect)renderer.ActiveShader!.Effects[0];effect.CloudOptions=new(slices,4096);
                long row=0;
                void Render()
                {
                    var samples=new double[128];
                    for(int side=0;side<2;side++) for(int b=0;b<16;b++) for(int c=0;c<3;c++)
                        samples[4*(side*16+b)+c]=.003+.04*Math.Pow(.5+.5*Math.Cos(12*(b+.5)/16+side*1.7+c*2.094),8);
                    renderer.PostAudio(new(samples,64,DateTime.UnixEpoch) {AnalysisRow=++row},2);renderer.Render(false);
                }
                for(int i=0;i<32;i++) {Render();renderer.ReadPixels(false);}
                var device=Field<ID3D11Device>(renderer,"device");var context=Field<ID3D11DeviceContext>(renderer,"context");
                using var timer=new Timestamps(device,context);var frames=new List<Dictionary<string,double>>();
                for(int i=0;i<21;i++)
                {
                    timer.Reset();renderer.TransportPassMarker=timer.Marker;
                    timer.Begin();Render();timer.End();frames.Add(timer.Resolve());renderer.TransportPassMarker=null;
                }
                var medians=frames.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(v=>v.GetValueOrDefault(k))));
                Console.WriteLine($"{mode}, {slices} slices: "+string.Join(", ",medians.Select(v=>$"{v.Key}={v.Value:F3}ms")));
                results.Add(new {mode,slices,lines=32,instances=4096,width=1280,height=720,milliseconds=medians});
                var images=new byte[3][];images[0]=renderer.ReadPixels(false);
                Save(images[0],1280,720,Path.Combine(output,$"{mode}-{slices}.png"));
                Set(renderer,"Medium.Density",0);Render();images[1]=renderer.ReadPixels(false);
                Set(renderer,"Medium.Density",.36f);effect.CloudOptions=new(slices,0);Render();images[2]=renderer.ReadPixels(false);
                if(mode=="benchmark" || mode=="production") isotropicImages[mode]=images;
            }
            for(int scene=0;scene<3;scene++)
            {
                var a=isotropicImages["benchmark"][scene];var b=isotropicImages["production"][scene];
                int max=0,different=0;long error=0;
                for(int i=0;i<a.Length;i++) {int e=Math.Abs(a[i]-b[i]);max=Math.Max(max,e);error+=e;if(e!=0) different++;}
                Console.WriteLine($"Infinite-line image agreement ({slices} slices, {scene}): max byte error={max}, differing channels={different}");
                comparisons.Add(new {slices,scene,maxByteError=max,differingChannels=different,meanByteError=(double)error/a.Length});
                if(max>2) throw new InvalidOperationException("Production infinite-line output differs unexpectedly from the benchmarked variant.");
            }
        }
        File.WriteAllText(Path.Combine(output,"comparison.json"),JsonSerializer.Serialize(new {
            adapter,note="1280x720, exposure -1.488, frozen scene, 32 warmup frames, median of 21 samples. Compare full scene, zero-density surfaces and fog-only. Identical all-active 16-bin/channel spectrum. Total excludes Present/readback. Compare finite isotropic, benchmarked infinite, and production infinite lines. Repeat production mode exposes timing variation.",results,comparisons
        },new JsonSerializerOptions {WriteIndented=true}));
    }
}

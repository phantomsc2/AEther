using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AEther.Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static partial class Program
{
    sealed record LightTypeCase(string Name,string Type,bool Shadows=false,bool Refresh=false);

    static void RunLightTypeProfile(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/mandellight-v4/light-types");
        string tree=Path.Combine(output,"Shaders");
        Directory.CreateDirectory(Path.Combine(tree,"Scenes"));
        foreach(string file in Directory.EnumerateFiles(Path.Combine(source,"Common"),"*.hlsl",SearchOption.AllDirectories))
        {
            string target=Path.Combine(tree,Path.GetRelativePath(source,file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);
        }
        string scene=Path.Combine(tree,"Scenes/MandelLightv4.hlsl");
        File.Copy(Path.Combine(source,"Scenes/MandelLightv4.hlsl"),scene,true);
        const int width=1280,height=720;
        bool half=args.Contains("--half-airlight");
        using var window=new Form {ClientSize=new(width,height),ShowInTaskbar=false};
        var clock=new Clock();
        using var renderer=new Renderer(window.Handle,width,height,32,60,shaderDirectory:tree,timeProvider:clock);
        renderer.SelectScene("MandelLightv4");
        if(renderer.ShaderError!=null)throw new InvalidOperationException(renderer.ShaderError);
        var device=Field<ID3D11Device>(renderer,"device");
        var context=Field<ID3D11DeviceContext>(renderer,"context");
        using var primer=new LightTypePrimer(device,context);
        using var timer=new Timestamps(device,context);
        Console.WriteLine(renderer.AdapterDescription+", 1280x720, "+(half ? "half" : "full")+" airlight");
        LightTypeCase[] cases=
        [
            new("no-lights","none"),new("point-unshadowed","point"),
            new("cone-unshadowed","cone"),new("ring-unshadowed","ring"),
            new("point-shadow-refresh","point",true,true),new("cone-shadow-refresh","cone",true,true),
            new("point-shadow-cached","point",true),new("cone-shadow-cached","cone",true)
        ];
        var frames=cases.ToDictionary(x=>x.Name,_=>new List<Dictionary<string,double>>());
        void Configure(LightTypeCase item)
        {
            renderer.ActiveShader!.Parameters!.Reset();clock.Reset();
            Set(renderer,"Shape.Speed",0);Set(renderer,"View.CameraSpeed",0);
            Set(renderer,"Lights.Count",0);Set(renderer,"Lights.Directional",item.Type=="cone");
            Set(renderer,"Lights.Enabled",item.Type is "point" or "cone");
            Set(renderer,"Lights.Shadows",item.Shadows);
            Set(renderer,"TonalLights.Enabled",false);Set(renderer,"TonalLights.Shadows",false);
            Set(renderer,"RingLight.Intensity",item.Type=="ring" ? .5f : 0);
            Set(renderer,"Quality.HalfResolutionAirlight",half);
            Set(renderer,"Filter.Enabled",false);
        }
        void Render(LightTypeCase item)
        {
            int bands=renderer.Audio.Bands;
            var spectrum=new double[8*bands];
            for(int i=0;i<spectrum.Length;i+=4){spectrum[i]=.025;spectrum[i+1]=.025;}
            renderer.PostAudio(new(spectrum,4*bands,DateTime.UnixEpoch){AnalysisRow=v4AudioRow++},2);
            // Force regeneration of exactly the same maps/geometry, so refresh
            // and cached cases differ only by map generation, not animation.
            if(item.Refresh)
            {
                object? transport=typeof(Renderer).GetField("rasterTransport",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(renderer);
                if(transport!=null)Array.Clear(Field<Array>(transport,"keys"));
            }
            clock.Advance();renderer.Render(false);
        }
        for(int round=0;round<3;round++)
        foreach(var item in (round%2==0 ? cases.AsEnumerable() : cases.Reverse()))
        {
            renderer.TransportPassMarker=null;Configure(item);
            bool needsLut=item.Type is "point" or "cone" && !item.Shadows;
            var wait=Stopwatch.StartNew();
            for(int i=0;i<8 || (needsLut&&!renderer.UnshadowedAirlightReady);i++)
            {
                Render(item);renderer.ReadPixels(false);
                if(wait.Elapsed.TotalSeconds>20)throw new TimeoutException("Point LUT generation timed out.");
            }
            for(int i=0;i<13;i++)
            {
                primer.Run();timer.Reset();renderer.TransportPassMarker=timer.Marker;
                timer.Begin();Render(item);timer.End();
                var frame=timer.Resolve();renderer.TransportPassMarker=null;
                int maps=frame.Keys.Count(k=>k.StartsWith("Raster light "));
                if(maps!=(item.Refresh ? 1 : 0))throw new InvalidOperationException($"Unexpected shadow refresh count in {item.Name}: {maps}.");
                frames[item.Name].Add(frame);
            }
            if(round==2)
            {
                byte[] pixels=renderer.ReadPixels(false);
                if(item.Type=="none" && pixels.Where((_,i)=>i%4!=3).Any(x=>x!=0))
                    throw new InvalidOperationException("No-light baseline was not black.");
                if(item.Type!="none" && !pixels.Where((_,i)=>i%4!=3).Any(x=>x>10))
                    throw new InvalidOperationException($"No visible illumination in {item.Name}.");
                Save(pixels,width,height,Path.Combine(output,item.Name+".png"));
            }
            Console.WriteLine($"Round {round+1} {item.Name}: total={Median(frames[item.Name].Select(x=>x["Total"])):F4}ms");
        }
        var results=cases.Select(item=>
        {
            var data=frames[item.Name];
            var median=data.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(data.Select(x=>x.GetValueOrDefault(k))));
            double shadow=Median(data.Select(x=>x.Where(y=>y.Key.StartsWith("Raster light ")).Sum(y=>y.Value)));
            return new {item.Name,item.Type,item.Shadows,item.Refresh,count=item.Type=="none" ? 0 : 1,
                samples=data.Count,shadowMs=shadow,milliseconds=median,
                totalP10=data.Select(x=>x["Total"]).Order().ElementAt(3),
                totalP90=data.Select(x=>x["Total"]).Order().ElementAt(35),frames=data};
        }).ToArray();
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(new
        {
            adapter=renderer.AdapterDescription,width,height,airWidth=half ? width/2 : width,airHeight=half ? height/2 : height,
            objects=128,shadowSamples=32,shadowResolution=512,coneHalfAngleDegrees=30,
            note="One active source per case. Same 128 static cubes/camera and live uniform S/T=.025. Default air/extinction, no source softening, RGBA32F HDR, no spatial filter. 39 GPU timestamp frames over three alternating-order rounds, at least 8 warmups per case/round. LUT ready before measurement. A constant independent compute primer precedes every frame outside timing to reduce GPU idle-clock variation. Refresh cases invalidate CPU cache keys before each render, generating identical maps each frame; cached cases retain them. Compilation, allocation, LUT generation, readback and primer are excluded. Point/cone source occupies the single ring position; the filament ring is centered at the origin with matching radius, orthogonal to both point-light rings. Filament ring shadows are unsupported, not estimated.",
            sceneSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(scene))),results
        },JsonOptions));
        foreach(var result in results)
            Console.WriteLine($"{result.Name}: shadows={result.shadowMs:F4}, "+string.Join(", ",result.milliseconds.Select(x=>$"{x.Key}={x.Value:F4}ms")));
    }

    // Same untimed workload for every case, including cheap LUT-only lights.
    sealed class LightTypePrimer : IDisposable
    {
        readonly ID3D11DeviceContext context;
        readonly ID3D11Texture2D texture;
        readonly ID3D11UnorderedAccessView view;
        readonly ID3D11ComputeShader shader;
        internal LightTypePrimer(ID3D11Device device,ID3D11DeviceContext context)
        {
            this.context=context;
            texture=device.CreateTexture2D(new Texture2DDescription
            {
                Width=256,Height=256,ArraySize=1,MipLevels=1,Format=Format.R32G32B32A32_Float,
                SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.UnorderedAccess
            });
            view=device.CreateUnorderedAccessView(texture);
            const string source="""
                RWTexture2D<float4> WarmOutput : register(u0);
                [numthreads(8,8,1)] void Warm(uint3 id:SV_DispatchThreadID) {
                    float4 v=float4(id.xy*.001,.13,.71);
                    [loop] for(int i=0;i<512;i++)v=frac(sqrt(v+1)*1.6180339+i*.00001);
                    WarmOutput[id.xy]=v;
                }
                """;
            shader=device.CreateComputeShader(Compiler.Compile(source,"Warm","LightTypePrimer.hlsl","cs_5_0").Span);
        }
        internal void Run()
        {
            context.CSSetShader(shader);context.CSSetUnorderedAccessView(0,view);
            context.Dispatch(32,32,1);
            context.CSSetUnorderedAccessView(0,null);context.CSSetShader(null);
        }
        public void Dispose(){shader.Dispose();view.Dispose();texture.Dispose();}
    }
}

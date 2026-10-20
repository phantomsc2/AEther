using System.Diagnostics;
using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunTonalRingProfile(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/mandellight-v4/tonal-ring");
        Directory.CreateDirectory(output);
        // Keep capture sources with the measured results.
        string tree=Path.Combine(output,"Shaders");Directory.CreateDirectory(Path.Combine(tree,"Scenes"));
        foreach(string file in Directory.EnumerateFiles(Path.Combine(source,"Common"),"*.hlsl",SearchOption.AllDirectories))
        {
            string target=Path.Combine(tree,Path.GetRelativePath(source,file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,true);
        }
        File.Copy(Path.Combine(source,"Scenes/MandelLightv4.hlsl"),Path.Combine(tree,"Scenes/MandelLightv4.hlsl"),true);
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var clock=new Clock();
        using var renderer=new Renderer(window.Handle,1280,720,32,60,shaderDirectory:tree,timeProvider:clock);
        renderer.SelectScene("MandelLightv4");
        var device=Field<ID3D11Device>(renderer,"device");var context=Field<ID3D11DeviceContext>(renderer,"context");
        Console.WriteLine(renderer.AdapterDescription);
        var results=new List<object>();
        bool samplingStudy=args.Contains("--spotlight-sampling");
        foreach(var item in new[]
        {
            (Name:"strobe-60",Count:60,Tone:0.0,Transient:.025),
            (Name:"tonal-1",Count:1,Tone:.025,Transient:0.0),
            (Name:"tonal-60",Count:60,Tone:.025,Transient:0.0),
            (Name:"tonal-96",Count:96,Tone:.025,Transient:0.0),
            (Name:"combined-60",Count:60,Tone:.025,Transient:.025),
        }.Where(item=>!samplingStudy || item.Name=="strobe-60"))
        {
            renderer.ActiveShader!.Parameters!.Reset();Set(renderer,"View.CameraSpeed",0);
            Set(renderer,"Lights.Shadows",true);Set(renderer,"TonalLights.Shadows",false);
            Set(renderer,"RingLight.Intensity",0); // Isolate the two measured point-light rings.
            Set(renderer,"Lights.Count",(item.Count-1)/(float)(RasterLightTransport.MaximumLights-1));clock.Reset();
            if(samplingStudy)Set(renderer,"Shape.Speed",0);
            void Render()
            {
                int bands=renderer.Audio.Bands;var spectrum=new double[8*bands];
                for(int channel=0;channel<2;channel++)for(int band=0;band<bands;band++)
                {
                    double f=(band+.5)/bands;
                    double shape=.25+.75*Math.Pow(.5+.5*Math.Cos(10*f+channel*1.7),2);
                    spectrum[4*(channel*bands+band)]=item.Tone*shape;
                    spectrum[4*(channel*bands+band)+1]=item.Transient*shape;
                }
                renderer.PostAudio(new(spectrum,4*bands,DateTime.UnixEpoch){AnalysisRow=v4AudioRow++},2);
                clock.Advance();renderer.Render(false);
            }
            var wait=Stopwatch.StartNew();
            for(int i=0;i<16 || (item.Tone>0&&!renderer.UnshadowedAirlightReady);i++)
            {
                Render();renderer.ReadPixels(false);
                if(wait.Elapsed.TotalSeconds>10)throw new TimeoutException("Tonal LUT did not become ready.");
                if(item.Tone>0&&!renderer.UnshadowedAirlightReady)Thread.Sleep(1);
            }
            var frames=new List<Dictionary<string,double>>();
            using var timer=new Timestamps(device,context);
            for(int i=0;i<41;i++)
            {
                renderer.TransportPassMarker=null;
                Set(renderer,"Quality.Samples",1);Render();
                Set(renderer,"Quality.Samples",.24409449f);Render();
                timer.Reset();renderer.TransportPassMarker=timer.Marker;
                timer.Begin();Render();timer.End();frames.Add(timer.Resolve());
            }
            renderer.TransportPassMarker=null;
            if(item.Transient==0 && frames.Any(frame=>frame.Keys.Any(name=>name.StartsWith("Raster light "))))
                throw new InvalidOperationException("Tonal illumination unexpectedly rendered shadow maps.");
            var median=frames.SelectMany(x=>x.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(x=>x.GetValueOrDefault(k))));
            Console.WriteLine(item.Name+": "+string.Join(", ",median.Select(x=>$"{x.Key}={x.Value:F3}ms")));
            results.Add(new {item.Name,item.Count,width=1280,height=720,lookupReady=renderer.UnshadowedAirlightReady,milliseconds=median});
            Save(renderer.ReadPixels(false),1280,720,Path.Combine(output,item.Name+".png"));
        }
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(new
        {
            adapter=renderer.AdapterDescription,
            note="Production path, 1280x720, default half-resolution airlight, 128 cubes, stationary camera. --spotlight-sampling freezes cubes and captures only strobe-60, with cached shadow maps. Tonal input uses live S, strobes use live T. 41 measured frames after warmup and matching LUT readiness; untimed high-sample plus default-sample render before each measured frame to reduce GPU idle downclocking. Compilation, LUT builds and readback excluded.",results,
        },JsonOptions));
    }
}

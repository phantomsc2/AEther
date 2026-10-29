using System.Diagnostics;
using System.Text.Json;
using AEther.Vortice;

internal static partial class Program
{
    static void RunProjectionCapture(string[] args)
    {
        var positional=args.Where(x=>!x.StartsWith("--")).ToArray();
        string source=Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output=Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/shared-projection");
        Directory.CreateDirectory(output);
        using var window=new Form {ClientSize=new(1280,720),ShowInTaskbar=false};
        var clock=new Clock();
        using var renderer=new Renderer(window.Handle,1280,720,32,60,shaderDirectory:source,timeProvider:clock,initialScene:"FilamentFlight");
        var results=new List<object>();
        foreach (string scene in new[] {"FilamentFlight","FoldFlight","SpectrumRoad","FluidFlight","PlaneFlight","MandelLightv2","MandelLightv5","MandelVolume"})
        foreach (var size in new[] {(Width:1280,Height:720),(Width:720,Height:1280),(Width:720,Height:720)})
        {
            renderer.SelectScene(scene); renderer.Resize(size.Width,size.Height);
            var wait=Stopwatch.StartNew();
            for (int frame=0;frame<8;frame++)
            {
                int bands=renderer.Audio.Bands; var spectrum=new double[8*bands];
                for (int c=0;c<2;c++) for (int band=0;band<bands;band++)
                {
                    double f=(band+.5)/bands,shape=.2+.8*Math.Pow(.5+.5*Math.Cos(10*f+c*1.7),2);
                    int at=4*(c*bands+band);
                    spectrum[at]=.025*shape; spectrum[at+1]=.015*shape; spectrum[at+2]=.02*shape; spectrum[at+3]=.04*shape;
                }
                renderer.PostAudio(new(spectrum,4*bands,DateTime.UnixEpoch){AnalysisRow=++rasterAudioRow},2);
                clock.Advance(); renderer.Render(false); renderer.ReadPixels(false);
                if (wait.Elapsed.TotalSeconds>60) throw new TimeoutException("Projection capture warmup timed out: "+scene);
            }
            byte[] pixels=renderer.ReadPixels(false);
            if (!pixels.Where((_,i)=>i%4!=3).Any(x=>x>0)) throw new InvalidOperationException("Empty projection capture: "+scene);
            string name=$"{scene}-{size.Width}x{size.Height}.png";
            Save(pixels,size.Width,size.Height,Path.Combine(output,name));
            results.Add(new {scene,width=size.Width,height=size.Height,file=name});
            Console.WriteLine(name);
        }
        File.WriteAllText(Path.Combine(output,"captures.json"),JsonSerializer.Serialize(new
        {
            renderer.AdapterDescription,
            note="Visual smoke captures only, no performance measurements. Live synthetic STB, default scene controls, eight frames per viewport plus atmosphere LUT readiness.",
            results,
        },new JsonSerializerOptions{WriteIndented=true}));
    }
}

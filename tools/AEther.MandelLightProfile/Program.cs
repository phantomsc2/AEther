using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;

// GPU timestamps surround draws, excluding compilation, CPU uploads and readback.
// Usage: dotnet run --project tools/AEther.MandelLightProfile -- <shader-root> <output-dir>
internal static partial class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--projection")) { RunProjectionCapture(args); return; }
        if (args.Contains("--point-lights")) { RunPointLightProfile(args); return; }
        if (args.Contains("--fluid-v4")) { RunFluidV4Profile(args); return; }
        if (args.Contains("--light-types")) { RunLightTypeProfile(args); return; }
        if (args.Contains("--airlight-lut")) { RunAirlightLutStudy(args); return; }
        if (args.Contains("--tonal-ring") || args.Contains("--spotlight-sampling")) { RunTonalRingProfile(args); return; }
        string source = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/mandellight-profile/current");
        Directory.CreateDirectory(output);
        string tree = Path.Combine(output, "Shaders");
        Directory.CreateDirectory(Path.Combine(tree, "Scenes"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "Common"), "*.hlsl", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(tree, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        string scene = args.Contains("--v2") ? "MandelLightv2" : "MandelLightv4";
        File.Copy(Path.Combine(source, "Scenes/"+scene+".hlsl"), Path.Combine(tree, "Scenes/"+scene+".hlsl"), true);
        if (scene=="MandelLightv2" && args.Contains("--reference"))
        {
            string file=Path.Combine(tree,"Scenes/"+scene+".hlsl");
            // Keep the loop bound uniform-driven: a literal 256 makes FXC spend
            // excessive time propagating constants through this shader's arrays.
            string original=File.ReadAllText(file);
            string reference=original.Replace("int samples=1+(int)round(63*Quality.Samples);","int samples=1+(int)round(255*Quality.Samples);");
            if (reference==original) throw new InvalidOperationException("Cannot override v2's sample budget.");
            File.WriteAllText(file,reference);
        }
        using var window = new Form { ClientSize = new(640, 360), ShowInTaskbar = false };
        var clock = new Clock();
        using var renderer = new Renderer(window.Handle, 640, 360, 32, 60, shaderDirectory: tree, timeProvider: clock);
        if (renderer.ShaderError != null) throw new InvalidOperationException(renderer.ShaderError);
        renderer.SelectScene(scene);
        var device = Field<ID3D11Device>(renderer, "device");
        var context = Field<ID3D11DeviceContext>(renderer, "context");
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        Console.WriteLine(adapter.Description.Description);
        if (scene=="MandelLightv4")
        {
            if (args.Contains("--shadows")) MeasureV4Shadows(renderer,device,context,clock,output,args.Contains("--small"));
            else if (args.Contains("--filter")) MeasureV4Filter(renderer,device,context,clock,output,args.Contains("--small"));
            else MeasureV4(renderer,device,context,clock,output,args.Contains("--quick"),args.Contains("--small"),args.Contains("--modes"));
            return;
        }
        if (scene=="MandelLightv2")
        {
            MeasureV2(renderer,device,context,clock,output,args.Contains("--quick") || args.Contains("--reference"),args.Contains("--reference"));
            return;
        }
    }

    static long v4AudioRow;
    static void RenderV4(Renderer renderer)
    {
        // Deterministic live-T fixture for this now audio-reactive scene. No
        // production fallback illumination is injected into the shader itself.
        int bands=renderer.Audio.Bands;
        var spectrum=new double[8*bands];
        for (int channel=0;channel<2;channel++)
        for (int band=0;band<bands;band++)
        {
            double f=(band+.5)/bands;
            spectrum[4*(channel*bands+band)+1]=.025*(.25+.75*Math.Pow(.5+.5*Math.Cos(10*f+channel*1.7),2));
        }
        renderer.PostAudio(new(spectrum,bands*4,DateTime.UnixEpoch) {AnalysisRow=v4AudioRow++},2);
        renderer.Render(false);
    }
    static void MeasureV4Shadows(Renderer renderer, ID3D11Device device, ID3D11DeviceContext context, Clock clock, string output, bool small)
    {
        int width=small ? 320 : 640,height=small ? 180 : 360;
        renderer.Resize(width,height);
        var results=new List<object>();
        foreach (bool air in new[] {true,false})
        foreach (int resolution in new[] {256,512})
        foreach (bool pcf in new[] {false,true})
        {
            string name=$"{(pcf ? "pcf" : "point")}{resolution}"+(air ? "" : "-no-air");
            renderer.ActiveShader!.Parameters!.Reset();
            Set(renderer,"Lights.Shadows",true);
            Set(renderer,"View.CameraSpeed",0);
            Set(renderer,"Quality.ShadowResolution",resolution==256 ? .5f : 1f);
            Set(renderer,"Quality.HardwarePCF",pcf);
            if (!air) Set(renderer,"Air.Density",0);
            clock.Reset();
            for (int i=0;i<8;i++) { clock.Advance(); RenderV4(renderer); renderer.ReadPixels(false); }
            var frames=new List<Dictionary<string,double>>();
            for (int i=0;i<11;i++)
            {
                using var timer=new Timestamps(device,context);
                renderer.TransportPassMarker=timer.Marker;
                clock.Advance(); timer.Begin(); RenderV4(renderer); timer.End();
                frames.Add(timer.Resolve()); renderer.TransportPassMarker=null;
            }
            var medians=frames.SelectMany(f=>f.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(f=>f.GetValueOrDefault(k))));
            Console.WriteLine(name+": "+string.Join(", ",medians.Select(p=>$"{p.Key}={p.Value:F3}ms")));
            results.Add(new {name,width,height,milliseconds=medians});
            Save(renderer.ReadPixels(false),width,height,Path.Combine(output,name+".png"));
        }
        File.WriteAllText(Path.Combine(output,"shadows.json"),JsonSerializer.Serialize(new
        {
            adapter=renderer.AdapterDescription,
            note="Same camera and time sequence, moving cubes, static 20-light ring, 32 air samples. Median of 11 frames after 8 warmups.",
            results,
        },new JsonSerializerOptions {WriteIndented=true}));
    }
    static void MeasureV4Filter(Renderer renderer, ID3D11Device device, ID3D11DeviceContext context, Clock clock, string output, bool small)
    {
        int width=small ? 320 : 640,height=small ? 180 : 360;
        renderer.Resize(width,height);
        var captures=new Dictionary<string,byte[]>();
        var timings=new Dictionary<string,Dictionary<string,double>>();
        foreach (string mode in new[] {"unfiltered","filtered","reference"})
        {
            if (mode=="reference")
            {
                string path=Path.Combine(renderer.ShaderDirectory,"Scenes","MandelLightv4.hlsl");
                string original=File.ReadAllText(path);
                string reference=original.Replace("int samples=1+(int)round(127*Quality.Samples);","int samples=1+(int)round(511*Quality.Samples);");
                if (reference==original) throw new InvalidOperationException("Cannot override v4 reference sample count.");
                File.WriteAllText(path,reference);
                if (!renderer.ReloadShaders()) throw new InvalidOperationException(renderer.ShaderError);
            }
            renderer.ActiveShader!.Parameters!.Reset();
            Set(renderer,"Quality.Samples",mode=="reference" ? 1f : 15f/127);
            Set(renderer,"Lights.Shadows",true);
            Set(renderer,"Quality.HalfResolutionAirlight",false);
            Set(renderer,"Sampling.RandomLight",false);
            Set(renderer,"Filter.Enabled",mode=="filtered");
            Set(renderer,"Shape.Speed",0);
            Set(renderer,"View.CameraSpeed",0);

            clock.Reset();
            for (int i=0;i<8;i++) { clock.Advance(); RenderV4(renderer); renderer.ReadPixels(false); }
            var frames=new List<Dictionary<string,double>>();
            for (int i=0;i<11;i++)
            {
                using var timer=new Timestamps(device,context);
                renderer.TransportPassMarker=timer.Marker;
                clock.Advance(); timer.Begin(); RenderV4(renderer); timer.End();
                frames.Add(timer.Resolve()); renderer.TransportPassMarker=null;
            }
            timings[mode]=frames.SelectMany(f=>f.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(f=>f.GetValueOrDefault(k))));
            captures[mode]=renderer.ReadPixels(false);
            Save(captures[mode],width,height,Path.Combine(output,mode+".png"));
            Console.WriteLine(mode+": "+string.Join(", ",timings[mode].Select(p=>$"{p.Key}={p.Value:F3}ms")));
        }
        var errors=new Dictionary<string,double>();
        foreach (string mode in new[] {"unfiltered","filtered"})
        {
            double squared=0;
            for (int i=0;i<captures[mode].Length;i++)
                if (i%4!=3) squared+=Math.Pow((captures[mode][i]-captures["reference"][i])/255.0,2);
            errors[mode]=Math.Sqrt(squared/(3*width*height));
            Console.WriteLine($"{mode}: display-RGB RMSE={errors[mode]:F6}");
        }
        File.WriteAllText(Path.Combine(output,"filter.json"),JsonSerializer.Serialize(new
        {
            adapter=renderer.AdapterDescription,width,height,samples=16,referenceSamples=512,
            note="Frozen scene; median of 11 GPU frames after 8 warmups. Display-RGB RMSE includes spatial-filter bias and reference sampling error.",
            milliseconds=timings,rmse=errors,
        },new JsonSerializerOptions {WriteIndented=true}));
    }
    static void MeasureV4(Renderer renderer, ID3D11Device device, ID3D11DeviceContext context, Clock clock, string output, bool quick, bool small, bool modes)
    {
        string[] cases=modes ? ["full-all-lights","random-light","half-airlight","both-modes"] : quick ? ["default"] : ["default","one-object","objects512","objects4096","one-light","static-maps",
            "samples128","cube512","equiangular","optical-depth","no-jitter","no-air","air-only","all-off","depth","shadow-cube","inside","1280x720"];
        var results=new List<object>();
        foreach (string name in cases)
        {
            renderer.ActiveShader!.Parameters!.Reset();
            int width=small ? 320 : name=="1280x720" ? 1280 : 640;
            int height=small ? 180 : name=="1280x720" ? 720 : 360;
            renderer.Resize(width,height);
            if (modes)
            {
                Set(renderer,"Sampling.RandomLight",name is "random-light" or "both-modes");
                Set(renderer,"Quality.HalfResolutionAirlight",name is "half-airlight" or "both-modes");
            }
            if (name=="one-object") Set(renderer,"Shape.Count",0);
            if (name=="objects512") Set(renderer,"Shape.Count",511f/4095);
            if (name=="objects4096") Set(renderer,"Shape.Count",1);
            if (name=="one-light") Set(renderer,"Lights.Count",0);
            if (name=="static-maps")
            {
                Set(renderer,"Shape.Speed",0);

            }
            if (name=="samples128") Set(renderer,"Quality.Samples",1);
            if (name=="cube512") Set(renderer,"Quality.ShadowResolution",1);
            if (name=="equiangular") Set(renderer,"Sampling.OpticalDepthWeight",0);
            if (name=="optical-depth") Set(renderer,"Sampling.OpticalDepthWeight",1);
            if (name=="no-jitter") Set(renderer,"Sampling.Jitter",false);
            if (name=="no-air") Set(renderer,"Air.Density",0);
            if (name=="air-only") Set(renderer,"Shape.Enabled",false);
            if (name=="all-off") Set(renderer,"Lights.Enabled",false);
            if (name=="depth") Set(renderer,"View.ShowDepth",true);
            if (name=="shadow-cube") Set(renderer,"View.ShowShadows",true);
            if (name=="inside") Set(renderer,"View.CameraDistance",.05f);
            clock.Reset();
            for (int i=0;i<8;i++) { clock.Advance(); RenderV4(renderer); renderer.ReadPixels(false); }
            var frames=new List<Dictionary<string,double>>();
            for (int i=0;i<11;i++)
            {
                using var timer=new Timestamps(device,context);
                renderer.TransportPassMarker=timer.Marker;
                clock.Advance(); timer.Begin(); RenderV4(renderer); timer.End();
                frames.Add(timer.Resolve()); renderer.TransportPassMarker=null;
            }
            var medians=frames.SelectMany(f=>f.Keys).Distinct().ToDictionary(k=>k,k=>Median(frames.Select(f=>f.GetValueOrDefault(k))));
            byte[] pixels=renderer.ReadPixels(false);
            if (name=="all-off" && pixels.Where((_,i)=>i%4!=3).Any(x=>x!=0))
                throw new InvalidOperationException("V4 retained radiance after disabling all lights.");
            Console.WriteLine(name+": "+string.Join(", ",medians.Select(p=>$"{p.Key}={p.Value:F3}ms")));
            results.Add(new {name,width,height,milliseconds=medians});
            Save(pixels,width,height,Path.Combine(output,name+".png"));
        }
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions {WriteIndented=true}));
    }
    static void MeasureV2(Renderer renderer, ID3D11Device device, ID3D11DeviceContext context, Clock clock, string output, bool quick, bool reference)
    {
        string[] cases=quick ? ["default"] : ["default","one-blob","sixteen-blobs","samples64","one-light","four-lights","inside","dense","emission","air-only","1280x720"];
        var results=new List<object>();
        foreach (string name in cases)
        {
            renderer.ActiveShader!.Parameters!.Reset();
            if (reference) Set(renderer,"Quality.Samples",1);
            int width=name=="1280x720" ? 1280 : 640, height=name=="1280x720" ? 720 : 360;
            renderer.Resize(width,height);
            if (name=="one-blob") Set(renderer,"Shape.Count",0);
            if (name=="sixteen-blobs") Set(renderer,"Shape.Count",1);
            if (name=="samples64") Set(renderer,"Quality.Samples",1);
            if (name=="one-light") Set(renderer,"Light2.Enabled",false);
            if (name=="four-lights") { Set(renderer,"Light3.Enabled",true); Set(renderer,"Light4.Enabled",true); }
            if (name=="inside") Set(renderer,"View.CameraDistance",.05f);
            if (name=="dense") Set(renderer,"Material.Density",1);
            if (name=="emission") { Set(renderer,"Emission.Enabled",true); Set(renderer,"Light.Enabled",false); Set(renderer,"Light2.Enabled",false); }
            if (name=="air-only") Set(renderer,"Material.Enabled",false);
            clock.Reset();
            for (int i=0;i<20;i++) { clock.Advance(); renderer.Render(false); renderer.ReadPixels(false); }
            var timings=new List<double>();
            for (int i=0;i<21;i++)
            {
                using var timer=new Timestamps(device,context);
                clock.Advance(); timer.Begin(); renderer.Render(false); timer.End();
                timings.Add(timer.Resolve()["Total"]);
            }
            double milliseconds=Median(timings);
            Console.WriteLine($"{name}: {milliseconds:F3}ms");
            results.Add(new { name,width,height,milliseconds });
            Save(renderer.ReadPixels(false),width,height,Path.Combine(output,name+".png"));
        }
        File.WriteAllText(Path.Combine(output,"timings.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions { WriteIndented=true }));
    }
    static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    static double Median(IEnumerable<double> values) { var a = values.Order().ToArray(); return a[a.Length / 2]; }
    static ShaderParameter Find(Renderer renderer, string path)
    {
        var items = renderer.ActiveShader!.Parameters!.Items;
        ShaderParameter result = null!;
        foreach (var part in path.Split('.')) { result = items.Single(p => p.Name == part); items = result.Children; }
        return result;
    }
    static void Set(Renderer renderer, string path, float value) => renderer.ActiveShader!.Parameters!.SetFloat(Find(renderer, path), value);
    static void Set(Renderer renderer, string path, bool value) => renderer.ActiveShader!.Parameters!.SetBoolean(Find(renderer, path), value);
    static void Save(byte[] rgba, int width, int height, string path)
    {
        var bgra = (byte[])rgba.Clone();
        for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        using var image = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var data = image.LockBits(new(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(bgra, 0, data.Scan0, bgra.Length); }
        finally { image.UnlockBits(data); }
        image.Save(path, ImageFormat.Png);
    }
    sealed class Clock : TimeProvider
    {
        long frame;
        public override long TimestampFrequency => 60;
        public override long GetTimestamp() => frame;
        internal void Advance() => frame++;
        internal void Reset() => frame = 0;
    }
    sealed class Timestamps : IDisposable
    {
        readonly ID3D11Device device;
        readonly ID3D11DeviceContext context;
        readonly ID3D11Query disjoint;
        readonly ID3D11Query[] pool;
        readonly List<(string Name, bool Start, ID3D11Query Query)> stamps = [];
        internal Timestamps(ID3D11Device device, ID3D11DeviceContext context)
        {
            this.device = device; this.context = context;
            disjoint = device.CreateQuery(new(QueryType.TimestampDisjoint));
            pool = Enumerable.Range(0, 80).Select(_ => device.CreateQuery(new QueryDescription(QueryType.Timestamp))).ToArray();
        }
        internal void Begin() { context.Begin(disjoint); Marker("Total", true); }
        internal void Reset() { stamps.Clear(); }
        internal void End() { Marker("Total", false); context.End(disjoint); context.Flush(); }
        internal void Marker(string name, bool start)
        {
            var query = pool[stamps.Count];
            stamps.Add((name, start, query)); context.End(query);
        }
        T Read<T>(ID3D11Query query) where T : unmanaged
        {
            var timeout = Stopwatch.StartNew();
            while (!context.GetData(query, out T value))
            {
                if (timeout.Elapsed.TotalSeconds > 30) throw new TimeoutException("GPU timestamp query did not finish.");
                Thread.Sleep(1);
            }
            context.GetData(query, out T result);
            return result;
        }
        internal Dictionary<string, double> Resolve()
        {
            var data = Read<QueryDataTimestampDisjoint>(disjoint);
            if (data.Disjoint) throw new InvalidOperationException("GPU timestamps were disjoint; rerun the profile.");
            var starts = new Dictionary<string, ulong>();
            var result = new Dictionary<string, double>();
            foreach (var stamp in stamps)
            {
                ulong time = Read<ulong>(stamp.Query);
                if (stamp.Start) starts[stamp.Name] = time;
                else result[stamp.Name] = result.GetValueOrDefault(stamp.Name) + 1000.0 * (time - starts[stamp.Name]) / data.Frequency;
            }
            return result;
        }
        public void Dispose() { foreach (var query in pool) query.Dispose(); disjoint.Dispose(); }
    }
}

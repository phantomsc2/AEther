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
        string source = Path.GetFullPath(args.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "artifacts/mandellight-profile/current");
        Directory.CreateDirectory(output);
        string tree = Path.Combine(output, "Shaders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tree, "Scenes"));
        foreach (var file in Directory.EnumerateFiles(Path.Combine(source, "Common"), "*.hlsl", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(tree, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
        string scene = "MandelLightv2";
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
        using var window = new Form { ClientSize = new(1280, 720), ShowInTaskbar = false };
        var clock = new Clock();
        using var renderer = new Renderer(window.Handle, 1280, 720, 32, 60, shaderDirectory: tree, timeProvider: clock);
        if (renderer.ShaderError != null) throw new InvalidOperationException(renderer.ShaderError);
        renderer.SelectScene(scene);
        var device = Field<ID3D11Device>(renderer, "device");
        var context = Field<ID3D11DeviceContext>(renderer, "context");
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        Console.WriteLine(adapter.Description.Description);
        if (scene=="MandelLightv2")
        {
            MeasureV2(renderer,device,context,clock,output,args.Contains("--quick") || args.Contains("--reference"),args.Contains("--reference"));
            return;
        }
    }

    static void MeasureV2(Renderer renderer, ID3D11Device device, ID3D11DeviceContext context, Clock clock, string output, bool quick, bool reference)
    {
        string[] cases=quick ? ["default"] : ["default","one-blob","sixteen-blobs","samples64","one-light","four-lights","inside","dense","emission","air-only","1280x720"];
        var results=new List<object>();
        foreach (string name in cases)
        {
            renderer.ActiveShader!.Parameters!.Reset();
            if (reference) Set(renderer,"Quality.Samples",1);
            int width=name=="1280x720" ? 1280 : 1920, height=name=="1280x720" ? 720 : 1080;
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

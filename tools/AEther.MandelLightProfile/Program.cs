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
        if (args.Contains("--fluid")) { RunFluidProfile(args); return; }
        RunPointLightProfile(args);
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
    static void SetSize(Renderer renderer, string path, uint value) => renderer.ActiveShader!.Parameters!.SetUnsigned(Find(renderer, path), value);
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

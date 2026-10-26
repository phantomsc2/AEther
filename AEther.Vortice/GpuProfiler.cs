using Vortice.Direct3D11;

namespace AEther.Vortice;

/// <summary>Delayed timestamp queries. The live path never flushes or waits for the GPU.</summary>
internal sealed class GpuProfiler : IDisposable
{
    static readonly string[] FluidStages = ["Fluid velocity", "Fluid pressure", "Fluid material"];
    sealed class Frame(ID3D11Device device) : IDisposable
    {
        internal readonly ID3D11Query Disjoint = device.CreateQuery(new(QueryType.TimestampDisjoint));
        internal readonly ID3D11Query[] Queries = Enumerable.Range(0, 64)
            .Select(_ => device.CreateQuery(new(QueryType.Timestamp))).ToArray();
        internal readonly List<(string Name, bool Start)> Events = new(64);
        internal bool Pending;
        internal bool Overflow;
        internal long Submitted;
        internal PipelineProfile? Profile;
        public void Dispose() { foreach (var query in Queries) query.Dispose(); Disjoint.Dispose(); }
    }
    readonly ID3D11DeviceContext context;
    readonly ID3DUserDefinedAnnotation? annotation;
    readonly Frame[] frames;
    readonly Dictionary<string, ulong> starts = new();
    readonly Dictionary<string, double> elapsed = new();
    readonly Dictionary<string, string> keys = new();
    Frame? active;
    bool frameOpen;
    bool annotated;
    int next;
    internal long SkippedFrames { get; private set; }
    internal long InvalidFrames { get; private set; }

    internal GpuProfiler(ID3D11Device device, ID3D11DeviceContext context)
    {
        this.context = context;
        annotation = context.QueryInterfaceOrNull<ID3DUserDefinedAnnotation>();
        frames = Enumerable.Range(0, 8).Select(_ => new Frame(device)).ToArray();
    }
    internal readonly struct FrameScope(GpuProfiler profiler) : IDisposable
    {
        public void Dispose() => profiler.EndFrame();
    }
    internal FrameScope BeginFrame(PipelineProfile? profile)
    {
        Poll();
        annotated = annotation?.Status == true;
        active = null;
        if (profile != null)
        {
            var frame = frames[next];
            if (!frame.Pending)
            {
                active = frame; frame.Profile = profile; frame.Events.Clear(); frame.Overflow = false;
                context.Begin(frame.Disjoint);
                next = (next + 1) % frames.Length;
            }
            else SkippedFrames++;
        }
        Marker("Frame", true);
        frameOpen = true;
        return new(this);
    }
    internal void EndFrame()
    {
        if (!frameOpen) return;
        frameOpen = false;
        Marker("Frame", false);
        if (active != null)
        {
            context.End(active.Disjoint);
            active.Submitted = System.Diagnostics.Stopwatch.GetTimestamp();
            active.Pending = true; active = null;
        }
    }
    internal void Annotate(string name, bool begin)
    {
        if (!annotated) return;
        if (begin) annotation?.BeginEvent(name); else annotation?.EndEvent();
    }
    internal void Marker(string name, bool begin)
    {
        Annotate(name, begin);
        if (active == null) return;
        int index = active.Events.Count;
        if (index == active.Queries.Length) { active.Overflow = true; return; }
        context.End(active.Queries[index]);
        active.Events.Add((name, begin));
    }
    internal void Poll()
    {
        foreach (var frame in frames)
        {
            if (!frame.Pending || !context.GetData(frame.Disjoint, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint data)) continue;
            if (data.Disjoint || data.Frequency == 0 || frame.Overflow) { frame.Pending = false; InvalidFrames++; continue; }
            // The final timestamp gates all preceding queries. Still check every result.
            if (!context.GetData(frame.Queries[frame.Events.Count - 1], AsyncGetDataFlags.DoNotFlush, out ulong _)) continue;
            starts.Clear(); elapsed.Clear();
            bool complete = true;
            for (int i = 0; i < frame.Events.Count; i++)
            {
                if (!context.GetData(frame.Queries[i], AsyncGetDataFlags.DoNotFlush, out ulong timestamp)) { complete = false; break; }
                var item = frame.Events[i];
                if (item.Start) starts[item.Name] = timestamp;
                else elapsed[item.Name] = elapsed.GetValueOrDefault(item.Name) + (timestamp - starts[item.Name]) * 1000.0 / data.Frequency;
            }
            if (!complete) continue;
            // Includes GPU queueing, driver query availability and CPU polling
            // cadence. This is query delivery latency, not shader execution time
            // or measured physical scanout latency.
            frame.Profile?.Elapsed("gpu.timing_delivery_ms", frame.Submitted);
            if (elapsed.ContainsKey("Fluid"))
                foreach (string name in FluidStages)
                    elapsed.TryAdd(name, 0);
            foreach (var (name, ms) in elapsed)
            {
                if (!keys.TryGetValue(name, out var key)) keys[name] = key = "gpu." + name.ToLowerInvariant().Replace(' ', '_') + "_ms";
                frame.Profile?.Record(key, ms);
            }
            frame.Pending = false;
        }
    }
    public void Dispose() { foreach (var frame in frames) frame.Dispose(); annotation?.Dispose(); }
}

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AEther.LoopbackBackendProbe;

internal static class Program
{
    static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            if (args.Length < 2) throw new ArgumentException("Usage: <naudio|cscore|bass|miniaudio> <baseline|before|after> [seconds=5] [variant=default] [tag]");
            using var timerResolution = new TimerResolution();
            var backend = args[0]; var mode = args[1];
            if (mode is not ("baseline" or "before" or "after")) throw new ArgumentException("Unknown mode");
            int seconds = args.Length > 2 ? int.Parse(args[2]) : 5;
            string variant = args.Length > 3 ? args[3] : "default";
            string tag = args.Length > 4 ? args[4] : DateTime.Now.ToString("yyyyMMdd-HHmmss");
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var query = endpoint.CreateAudioClient();
            var format = query.MixFormat;
            var supported = query.GetSharedModeEnginePeriod(format);
            Console.WriteLine($"{backend}/{mode}/{variant}: {endpoint.FriendlyName}, {format}");
            var periods = new List<object>();
            void Snapshot(string stage) { var p = EnginePeriod.Read(endpoint); periods.Add(new {stage, p.Frames, p.SampleRate, p.Milliseconds}); Console.WriteLine($"  Engine {stage}: {p.Frames} frames / {p.Milliseconds:F4} ms"); }
                        if (mode is "baseline" or "after")
            {
                // Windows can retain a short period briefly after the previous process exits.
                for (int attempt=0; attempt<20 && EnginePeriod.Read(endpoint).Milliseconds<9.9; attempt++) Thread.Sleep(500);
                if(EnginePeriod.Read(endpoint).Milliseconds<9.9) throw new Exception("Engine has not returned to its 10 ms baseline; do not label this a fresh baseline/startup-order test.");
            }
                        if (backend == "miniaudio")
            {
                using var consoleEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Console);
                if(consoleEndpoint.ID!=endpoint.ID) throw new Exception("miniaudio default endpoint differs from the selected multimedia endpoint; this comparison would be invalid.");
            }
            Snapshot("initial");
            using var heartbeat = new WasapiPlayerBuilder().WithDevice(endpoint).WithMmcssThreadPriority("Pro Audio").Build();
            heartbeat.Init(new Silence(format)); heartbeat.Play();
            WasapiPlayer? helper = null;
            void Force() {
                helper = new WasapiPlayerBuilder().WithDevice(endpoint).WithLowLatency(required: true).WithMmcssThreadPriority("Pro Audio").Build();
                helper.Init(new Silence(format)); helper.Play();
                if (!helper.LowLatencyActive) throw new Exception("Low-latency render stream was not engaged");
                Console.WriteLine($"  Helper active: {helper.LowLatencyActive}, {helper.LatencyMilliseconds} ms");
                Snapshot("helper-started");
            }
            var capture = new CaptureMeasurements();
            try
            {
                if (mode == "before") Force();
                using ICapture adapter = backend switch
                {
                    "naudio" => new NAudioCapture(endpoint, capture),
                    "cscore" => new CSCoreCapture(endpoint.ID, capture, variant),
                    "bass" => new BassCapture(endpoint.ID, capture, variant),
                    "miniaudio" => new MiniAudioCapture(capture, variant, format.SampleRate, format.Channels),
                    _ => throw new ArgumentException("Unknown backend")
                };
                adapter.Start(); Snapshot("capture-started");
                if (mode == "after") Force();
                Thread.Sleep(1000); // Exclude initialization, JIT, and the period transition.
                Snapshot("measurement-start"); capture.Begin();
                Thread.Sleep(TimeSpan.FromSeconds(seconds));
                var data = capture.End(); Snapshot("measurement-end"); adapter.Stop();
                string output = Path.Combine("artifacts", "capture-backends", tag);
                Directory.CreateDirectory(output);
                string stem = $"{backend}-{mode}-{variant}";
                File.WriteAllLines(Path.Combine(output, stem + ".csv"), new[]{"arrival_ticks,frames,callback_duration_ms,arrival_gap_ms"}.Concat(data.Select((r,i) => FormattableString.Invariant($"{r.Ticks},{r.Frames},{r.Frames*1000.0/adapter.SampleRate:R},{(i==0?0:Stopwatch.GetElapsedTime(data[i-1].Ticks,r.Ticks).TotalMilliseconds):R}"))));
                var gaps = data.Skip(1).Select((r,i) => Stopwatch.GetElapsedTime(data[i].Ticks,r.Ticks).TotalMilliseconds).ToArray();
                var blocks = data.Select(r=>r.Frames*1000.0/adapter.SampleRate).ToArray();
                // Adjacent callbacks less than 0.25 ms apart are one delivery burst, not evidence of fresher audio.
                var bursts = new List<double>(); double burst=0;
                for(int i=0;i<data.Length;i++) {if(i>0 && gaps[i-1]>.25) {bursts.Add(burst);burst=0;} burst+=blocks[i];}
                if(burst>0)bursts.Add(burst);
                object Stats(double[] values) {var a=values.Order().ToArray(); return a.Length==0?new{}:(object)new{min=a[0],median=a[a.Length/2],p95=a[(int)((a.Length-1)*.95)],max=a[^1]};}
                double elapsed=data.Length<2?0:Stopwatch.GetElapsedTime(data[0].Ticks,data[^1].Ticks).TotalSeconds;
                var result = new {backend,mode,variant,endpoint=endpoint.FriendlyName,endpointId=endpoint.ID,format=format.ToString(),timerResolutionMs=1,sampleRate=adapter.SampleRate,requestedMinimumFrames=supported.ChooseLowestLatencyPeriod(),callbackCount=data.Length,callbacksPerSecond=elapsed>0?(data.Length-1)/elapsed:0,callbackMs=Stats(blocks),arrivalGapMs=Stats(gaps),deliveryBurstMs=Stats(bursts.ToArray()),burstCount=bursts.Count,shortGaps=gaps.Count(x=>x<.25),periods,adapter.Details};
                string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
                File.WriteAllText(Path.Combine(output,stem+".json"),json);
                Console.WriteLine(json);
                if(data.Length<5)throw new Exception("Not enough capture data");
            }
            finally {helper?.Dispose(); heartbeat.Stop();}
            return 0;
        }
        catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
internal sealed class Silence(WaveFormat format) : IWaveProvider
{
    public WaveFormat WaveFormat => format;
    public int Read(Span<byte> buffer) {buffer.Clear();return buffer.Length;}
}
internal interface ICapture : IDisposable
{
    int SampleRate {get;}
    string Details {get;}
    void Start(); void Stop();
}
internal readonly record struct CapturedBlock(long Ticks,int Frames);
internal sealed class CaptureMeasurements
{
    readonly object gate=new(); readonly List<CapturedBlock> blocks=new(20000); bool recording;
    public void Add(int frames) {long now=Stopwatch.GetTimestamp();lock(gate) if(recording&&frames>0)blocks.Add(new(now,frames));}
    public void Begin(){lock(gate){blocks.Clear();recording=true;}}
    public CapturedBlock[] End(){lock(gate){recording=false;return blocks.ToArray();}}
}
internal static class EnginePeriod
{
    public readonly record struct Period(uint Frames,int SampleRate){public double Milliseconds=>Frames*1000.0/SampleRate;}
    public static Period Read(MMDevice endpoint)
    {
        using var client=endpoint.CreateAudioClient();
        // NAudio exposes supported periods publicly but currently lacks the current-period wrapper.
        var field=typeof(AudioClient).GetField("audioClientInterface3",BindingFlags.NonPublic|BindingFlags.Instance)!;
        object[] args={IntPtr.Zero,0u};
        try {
            int hr=(int)field.FieldType.GetMethod("GetCurrentSharedModeEnginePeriod")!.Invoke(field.GetValue(client),args)!;
            Marshal.ThrowExceptionForHR(hr);
            return new((uint)args[1],Marshal.ReadInt32((IntPtr)args[0],4));
        }
        finally {if((IntPtr)args[0]!=IntPtr.Zero)Marshal.FreeCoTaskMem((IntPtr)args[0]);}
    }
}

internal sealed class TimerResolution : IDisposable
{
    [DllImport("winmm")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm")] static extern uint timeEndPeriod(uint ms);
    public TimerResolution(){if(timeBeginPeriod(1)!=0)throw new Exception("Could not request 1 ms timer resolution");}
    public void Dispose()=>timeEndPeriod(1);
}
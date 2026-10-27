using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using AEther.WinUI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AEther.ChartProfile;

/// <summary>Measure the actual hub with its normal renderer, analysis, chart and window lifecycle.</summary>
internal sealed class HubProfile(Application app)
{
    readonly string[] phases = ["scene", "chart", "spectrum", "full", "scene-repeat"];
    readonly List<string> results = ["phase,seconds,process_cpu_cores,ui_cpu_cores,allocated_bytes_per_second,gen0,private_mib,chart_updates"];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly Stopwatch phaseClock = new();
    readonly string output = Path.GetFullPath(Path.Combine("artifacts", "hub-profile",
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tag="))?[6..] ?? DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    MainWindow host = null!;
    CanvasWindow scene = null!;
    AnalysisView analysis = null!;
    FrameTimeView chart = null!;
    Session session = null!;
    Process process = null!;
    ProcessThread ui = null!;
    int phase = -1, gen0;
    long allocated, chartUpdates;
    TimeSpan processCpu, uiCpu;
    bool recording, initialized, finishing;

    public void Start()
    {
        Directory.CreateDirectory(output);
        host = new MainWindow(() => new SyntheticAudio());
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        scene = Field<CanvasWindow>("canvasWindow");
        analysis = Field<AnalysisView>("analysis");
        chart = Field<FrameTimeView>("frameTime");
        process = Process.GetCurrentProcess();
        ui = process.Threads.Cast<ProcessThread>().Single(t => t.Id == GetCurrentThreadId());
        app.UnhandledException += (_, e) => { e.Handled = true; Finish(e.Exception); };
        host.Closed += (_, _) => { timer.Stop(); app.Exit(); };
        var startup = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            if (finishing) return;
            try
            {
                if (!initialized)
                {
                    session = Field<Session>("session");
                    if (session == null)
                    {
                        if (startup.Elapsed.TotalSeconds > 45) throw new Exception("Hub startup timed out.");
                        return;
                    }
                    host.AppWindow.ResizeClient(new SizeInt32(1280, 720));
                    scene.AppWindow.ResizeClient(new SizeInt32(1280, 720));
                    var work = DisplayArea.GetFromWindowId(host.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                    host.AppWindow.Move(new PointInt32(work.X, work.Y));
                    scene.AppWindow.Move(new PointInt32(work.X + Math.Max(0, work.Width - 1280), work.Y));
                    if (!scene.IsVisible) throw new Exception("Launch the hub probe with --scene=<scene>.");
                    initialized = true;
                    NextPhase();
                    return;
                }
                if (analysis.Error != null) throw new Exception(analysis.Error);
                if (!scene.IsVisible) throw new Exception("Scene must remain visible throughout the hub comparison.");
                // First phase includes shader/JIT warmup; subsequent phases recreate only the secondary surface.
                var warmup = phase == 0 ? 6 : 2;
                if (!recording && phaseClock.Elapsed.TotalSeconds >= warmup)
                {
                    session.Analyzer.Profile.Start();
                    processCpu = process.TotalProcessorTime;
                    uiCpu = ui.TotalProcessorTime;
                    allocated = GC.GetTotalAllocatedBytes(false);
                    gen0 = GC.CollectionCount(0);
                    chartUpdates = chart.CompletedUpdates;
                    phaseClock.Restart();
                    recording = true;
                }
                if (recording && phaseClock.Elapsed.TotalSeconds >= 8)
                {
                    var seconds = phaseClock.Elapsed.TotalSeconds;
                    var profile = session.Analyzer.Profile;
                    profile.Describe("Phase", phases[phase]);
                    profile.Describe("Scene", session.Renderer.SceneName);
                    profile.Describe("Adapter", session.Renderer.AdapterDescription);
                    profile.Describe("Scene resolution", "1280x720");
                    profile.Describe("Hub client resolution", "1280x720");
                    profile.Describe("Spectrum render resolution", $"{analysis.PreviewPixelSize.Width}x{analysis.PreviewPixelSize.Height}");
                    profile.Describe("Plotted points", chart.PlottedPointCount.ToString());
                    profile.Describe("Source", "48 kHz timestamped synthetic stereo, same analyzer for all phases");
                    profile.Describe("Timing", "Release, compositor paced, 8 s sample after warmup; GPU Debug is the entire secondary output");
                    process.Refresh();
                    results.Add(FormattableString.Invariant($"{phases[phase]},{seconds:F3},{(process.TotalProcessorTime - processCpu).TotalSeconds / seconds:F4},{(ui.TotalProcessorTime - uiCpu).TotalSeconds / seconds:F4},{(GC.GetTotalAllocatedBytes(false) - allocated) / seconds:F0},{GC.CollectionCount(0) - gen0},{process.PrivateMemorySize64 / 1048576.0:F1},{chart.CompletedUpdates - chartUpdates}"));
                    profile.Save(Path.Combine(output, phases[phase]));
                    File.WriteAllLines(Path.Combine(output, "process.csv"), results);
                    NextPhase();
                }
            }
            catch (Exception error) { Finish(error); }
        };
        host.Activate();
        timer.Start();
    }

    void NextPhase()
    {
        if (++phase == phases.Length) { Finish(null); return; }
        bool visible = !phases[phase].StartsWith("scene", StringComparison.Ordinal);
        if (visible) host.Activate();
        else host.AppWindow.Hide();
        analysis.Attach(phases[phase] is "spectrum" or "full" ? session.Renderer : null);
        chart.Attach(phases[phase] is "chart" or "full" ? session.Analyzer.Profile : null);
        recording = false;
        phaseClock.Restart();
    }

    void Finish(Exception? error)
    {
        if (finishing) return;
        finishing = true;
        timer.Stop();
        if (error != null)
        {
            File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString());
            Environment.ExitCode = 1;
        }
        host.RequestClose();
    }

    sealed class SyntheticAudio : SampleSource, ITimedSampleSource
    {
        readonly ManualResetEvent stopped = new(false);
        Thread? thread;
        public override SampleFormat Format { get; } = new(SampleType.Float32.Instance, 48000, 2);
        public event TimedSampleDataHandler? TimedDataAvailable;
        public override void Start()
        {
            stopped.Reset();
            thread = new Thread(() =>
            {
                var samples = new float[480 * 2];
                var origin = Stopwatch.GetTimestamp() - AudioClock.Frames(480, 48000);
                long frame = 0;
                while (!stopped.WaitOne(0))
                {
                    for (int i = 0; i < 480; i++)
                    {
                        double t = (frame + i) / 48000.0;
                        double pulse = Math.Exp(-(t % .4) * 40);
                        samples[2 * i] = (float)(.025 * (Math.Sin(t * Math.Tau * 220) + Math.Sin(t * Math.Tau * 440)) + .04 * pulse * Math.Sin(t * Math.Tau * 80));
                        samples[2 * i + 1] = (float)(.025 * (Math.Sin(t * Math.Tau * 330) + Math.Sin(t * Math.Tau * 660)) + .04 * pulse * Math.Sin(t * Math.Tau * 120));
                    }
                    var timestamp = origin + AudioClock.Frames(frame, 48000);
                    TimedDataAvailable?.Invoke(this, MemoryMarshal.AsBytes(samples.AsSpan()),
                        new AudioPacketTiming(timestamp, frame, AudioTimestampQuality.Estimated));
                    frame += 480;
                    var wait = TimeSpan.FromSeconds(AudioClock.Seconds(origin + AudioClock.Frames(frame + 480, 48000) - Stopwatch.GetTimestamp()));
                    if (wait > TimeSpan.Zero && stopped.WaitOne(wait)) break;
                }
            }) { IsBackground = true, Name = "Hub probe audio" };
            thread.Start();
        }
        public override void Stop() { stopped.Set(); thread?.Join(); thread = null; }
        public override void Dispose() { Stop(); stopped.Dispose(); }
    }

    [DllImport("kernel32.dll")]
    static extern int GetCurrentThreadId();
}

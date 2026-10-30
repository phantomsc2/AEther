using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AEther;
using AEther.Smoke;
using AEther.Vortice;
using AEther.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

internal static partial class Program
{
    // Real SwapChainPanel + compositor pacing, unlike the offscreen solver benchmark.
    static void ProfilePresentation(string[] args)
    {
        string Option(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..] ?? fallback;
        string scene = Option("--scene", "MandelFluid");
        string output = Path.GetFullPath(Option("--output", "artifacts/presentation/current"));
        int width = int.Parse(Option("--width", "1280")), height = int.Parse(Option("--height", "720"));
        int seconds = int.Parse(Option("--seconds", "10"));
        if (width < 1280 || height < 720 || seconds < 1) throw new ArgumentOutOfRangeException(nameof(args));
        Exception? failure = null;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new WindowCheckApplication();
            app.StartChecks = () =>
            {
                var canvas = new SwapChainPanel();
                var host = new Microsoft.UI.Xaml.Window { Content = canvas, Title = "AEther presentation profile" };
                var screens = System.Windows.Forms.Screen.AllScreens;
                string monitor = Option("--monitor", "primary");
                var screen = monitor == "primary" ? System.Windows.Forms.Screen.PrimaryScreen!
                    : monitor == "secondary" ? screens.First(s => !s.Primary)
                    : screens.Single(s => s.DeviceName.Equals(monitor, StringComparison.OrdinalIgnoreCase));
                host.AppWindow.MoveAndResize(new RectInt32(screen.Bounds.X, screen.Bounds.Y, width, height));
                Renderer? renderer = null;
                RenderWorker? worker = null;
                // Explicit reference mode reproduces the old compositor-only
                // loop for latency/pacing comparisons; production always gates DXGI.
                var bypassReady = new ManualResetEvent(true);
                var profile = new PipelineProfile();
                var watch = new Stopwatch();
                bool recording = false;
                TimeSpan cpuStart = default;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                void Finish(Exception? error)
                {
                    timer.Stop(); worker?.Dispose(); worker = null; bypassReady.Dispose();
                    if (renderer != null)
                    {
                        double elapsed = Math.Max(.001, watch.Elapsed.TotalSeconds - 2);
                        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds / elapsed;
                        profile.Describe("Adapter", renderer.AdapterDescription);
                        profile.Describe("Resolution", $"{width}x{height}");
                        profile.Describe("Scene", scene);
                        profile.Describe("Monitor", screen.DeviceName + " " + screen.Bounds);
                        profile.Describe("Display mode", DisplayMode(screen.DeviceName));
                        profile.Describe("Fullscreen", args.Contains("--fullscreen").ToString());
                        profile.Describe("Pacing", Option("--pacing", "compositor"));
                        profile.Describe("Swap-chain readiness", args.Contains("--bypass-swap-ready") ? "bypassed (reference only)" : "enabled");
                        profile.Describe("Path", "WinUI SwapChainPanel, compositor pacing, synthetic timestamped audio");
                        profile.Describe("Fluid work", renderer.FluidWork);
                        profile.Describe("Process CPU core equivalents", cpu.ToString("F3"));
                        Console.WriteLine(profile.Save(output));
                        Console.WriteLine($"{scene}, {renderer.AdapterDescription}, {width}x{height}");
                        Console.WriteLine($"Monitor: {screen.DeviceName}, {DisplayMode(screen.DeviceName)}");
                        foreach (string stage in new[] { "gpu.frame_ms", "gpu.output_ms", "gpu.timing_delivery_ms", "gpu.fluid_ms", "render.frame_interval_ms", "render.display_interval_ms", "render.raw_display_interval_ms", "render.display_lead_ms", "render.pacing_wait_ms", "render.draw_cpu_ms", "render.present_wait_ms" })
                            Console.WriteLine($"{stage}: {profile.Recent(stage)}");
                        renderer.Dispose(); renderer = null;
                    }
                    failure = error; host.Close(); app.Exit();
                }
                void StartIfReady()
                {
                    try
                    {
                        if (renderer != null || !canvas.IsLoaded) return;
                        var actual = System.Windows.Forms.Screen.FromHandle(WinRT.Interop.WindowNative.GetWindowHandle(host));
                        if (actual.DeviceName != screen.DeviceName) throw new InvalidOperationException($"Expected {screen.DeviceName}, window is on {actual.DeviceName}.");
                        renderer = new Renderer(WinRT.Interop.WindowNative.GetWindowHandle(host), width, height, 97, 1000,
                            initialScene: scene,
                            attachSwapChain: chain => SwapChainHost.Attach(canvas, chain));
                        renderer.SetCompositionScale(canvas.CompositionScaleX, canvas.CompositionScaleY);
                        renderer.RawPresentationClock = args.Contains("--raw-frame-clock");
                        renderer.FixedFluidMaterialCadence = args.Contains("--fixed-fluid-material");
                        if (args.Contains("--fast") && scene == "MandelFluid")
                        {
                            var parameters = renderer.ActiveShader!.Effects[0].Parameters!;
                            var fluid = parameters.Items.Single(p => p.Name == "Fluid");
                            foreach (var setting in new[] { ("EmissionVelocity", 1f), ("VelocityGain", .75f), ("Curl", .75f) })
                                parameters.SetFloat(fluid.Children.Single(p => p.Name == setting.Item1), setting.Item2);
                        }
                        renderer.Audio.Profile = profile;
                        long origin = Stopwatch.GetTimestamp(), row = 0;
                        var samples = new double[97 * 8];
                        for (int i = 0; i < samples.Length; i += 4)
                        { samples[i] = .08; samples[i + 1] = .1; samples[i + 2] = .06; samples[i + 3] = .2; }
                        bool compositor = CompositorClock.IsSupported && Option("--pacing", "compositor") != "dxgi";
                        worker = new RenderWorker(args.Contains("--bypass-swap-ready") ? bypassReady : renderer.FrameReady, () =>
                        {
                            long end = (long)(AudioClock.Seconds(Stopwatch.GetTimestamp() - origin) * 1000) - 6;
                            // Source rows retain their timestamps even when a display frame is late.
                            while (row < end)
                            {
                                renderer.PostAudio(new(samples, 97 * 4, DateTime.UnixEpoch)
                                {
                                    AnalysisRow = row, StreamClock = new(0, origin, 48000),
                                    SampleStartTimestamp = origin + AudioClock.Frames(row, 1000),
                                    SampleEndTimestamp = origin + AudioClock.Frames(row + 1, 1000),
                                }, 2);
                                row++;
                            }
                            renderer.Render(syncInterval: 0u);
                        }, error => host.DispatcherQueue.TryEnqueue(() => Finish(error)),
                            waitForTick: compositor ? CompositorClock.Wait : null,
                            waited: ms => profile.Record("render.pacing_wait_ms", ms));
                        watch.Start(); worker.SetEnabled(true); timer.Start();
                    }
                    catch (Exception error) { Finish(error); }
                }
                canvas.Loaded += (_, _) => StartIfReady();
                timer.Tick += (_, _) =>
                {
                    if (!recording && watch.Elapsed.TotalSeconds >= 2)
                    {
                        cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
                        profile.Start(); recording = true;
                    }
                    if (watch.Elapsed.TotalSeconds >= seconds + 2) Finish(null);
                };
                host.Activate();
                if (args.Contains("--fullscreen")) host.AppWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
            };
        });
        if (failure != null) throw new InvalidOperationException("Presentation profiling failed.", failure);
    }

    static string DisplayMode(string monitor)
    {
        // DEVMODEW's fixed layout; query only, never change the user's display mode.
        var mode = Marshal.AllocHGlobal(220);
        try
        {
            for (int i = 0; i < 220; i += 4) Marshal.WriteInt32(mode, i, 0);
            Marshal.WriteInt16(mode, 68, 220);
            return EnumDisplaySettings(monitor, -1, mode)
                ? $"{Marshal.ReadInt32(mode, 172)}x{Marshal.ReadInt32(mode, 176)}, {Marshal.ReadInt32(mode, 184)} Hz"
                : "mode unavailable";
        }
        finally { Marshal.FreeHGlobal(mode); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettings(string device, int mode, IntPtr settings);
}

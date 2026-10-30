using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using AEther.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace AEther.ChartProfile;

public partial class App : Application
{
    Window? window;
    readonly string output = Path.GetFullPath(Path.Combine("artifacts", "chart-profile",
        Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tag="))?[6..] ?? "default"));

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "error.txt"), e.Exception.ToString());
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Contains("--hub"))
        {
            new HubProfile(this).Start();
            return;
        }
        Directory.CreateDirectory(output);
        var arguments = Environment.GetCommandLineArgs();
        var windowSeconds = FrameTimeView.HistorySeconds;
        var profile = new PipelineProfile();
        window = new Window { Title = "AEther frame-time graph probe" };
        var view = new FrameTimeView(window);


        var refreshRate = arguments.Contains("--fast") ? 10 : 1 / view.RefreshInterval.TotalSeconds;
        view.RefreshInterval = TimeSpan.FromSeconds(1.0 / refreshRate);
        var layout = arguments.Contains("--layout");
        AnalysisView? analysis = null;
        SettingsPanel? settings = null;
        if (layout)
        {
            analysis = new AnalysisView(window);
            settings = new SettingsPanel(new AudioPanel(), new GraphicsPanel(), new ShaderPanel(), analysis, view);
        }
        window.Content = (UIElement?)settings ?? view;
        // Both the chart-only and layout probes use at least 1280x720 client pixels.
        window.AppWindow.ResizeClient(new SizeInt32(1280, 720));
        view.Attach(profile);
        const double inputRate = 60;
        long generated = 0, analyzed = 0;
        var origin = Stopwatch.GetTimestamp() - (long)(windowSeconds * Stopwatch.Frequency);
        void GenerateUntil(long timestamp)
        {
            while (origin + (long)(generated * Stopwatch.Frequency / inputRate) <= timestamp)
            {
                var time = origin + (long)(generated * Stopwatch.Frequency / inputRate);
                var duration = generated % 120 == 0 ? 40 : 16.67 + 2 * Math.Sin(generated * .2);
                profile.Record("render.frame_interval_ms", duration, time);
                profile.Record("render.draw_cpu_ms", .6 + .15 * Math.Sin(generated * .17), time);
                profile.Record("gpu.frame_ms", 2.5 + .5 * Math.Sin(generated * .13), time);
                profile.Record("capture.queue_ms", .02 + .01 * Math.Sin(generated * .11), time);
                profile.Record("render.pacing_wait_ms", 12 + 3 * Math.Sin(generated * .11), time);
                generated++;
            }
            while (origin + (long)(analyzed * Stopwatch.Frequency / 1000.0) <= timestamp)
            {
                var time = origin + (long)(analyzed * Stopwatch.Frequency / 1000.0);
                profile.Record("analysis.processing_ms", analyzed % 2000 == 0 ? 3 : .08 + .02 * Math.Sin(analyzed * .17), time);
                profile.Record("render.analysis_handoff_ms", .3 + .1 * Math.Sin(analyzed * .11), time);
                analyzed++;
            }
        }
        GenerateUntil(Stopwatch.GetTimestamp());
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / 60) };
        bool recording = false, finishing = false;
        TimeSpan initialCpu = default;
        long initialUpdates = 0;
        var process = Process.GetCurrentProcess();
        timer.Tick += async (_, _) =>
        {
            if (finishing) return;
            try
            {
                GenerateUntil(Stopwatch.GetTimestamp());
                if (!recording && watch.Elapsed.TotalSeconds >= 3)
                {
                    profile.Start();
                    initialCpu = process.TotalProcessorTime;
                    initialUpdates = view.CompletedUpdates;
                    recording = true;
                }
                if (watch.Elapsed.TotalSeconds < 13) return;
                finishing = true;
                timer.Stop();
                var seconds = watch.Elapsed.TotalSeconds - 3;
                if (view.PointCount < windowSeconds * 60 - 5 || view.LostSamples != 0 || view.CompletedUpdates - initialUpdates < Math.Max(1, 5 * refreshRate))
                    throw new Exception($"Chart stalled or lost samples: {view.PointCount} points, {view.CompletedUpdates} draws, {view.LostSamples} lost.");
                if (view.RenderPointCount != view.PointCount || view.GpuPointCount != view.PointCount
                    || view.AnalysisPointCount < windowSeconds * 1000 - 20
                    || view.SchedulingPointCounts[2] != view.AnalysisPointCount
                    || view.SchedulingPointCounts.Where((count, index) => index != 2).Any(count => count != view.PointCount))
                    throw new Exception("All native frame and 1000 Hz analysis/handoff samples must be retained, including sub-millisecond values.");
                if (!view.CurveNames.SequenceEqual(new[] { "Analysis", "Render", "GPU", "Frame", "Queue", "Handoff", "Pacing" })
                    || Math.Abs(view.Processing.ActualHeight - view.Scheduling.ActualHeight) > 1
                    || view.Processing.PresentTime != view.Scheduling.PresentTime
                    || view.Processing.LowerLimit >= -1 || view.Scheduling.LowerLimit >= -1
                    || view.Processing.UpperLimit < Math.Log10(3) || view.Scheduling.UpperLimit < Math.Log10(40))
                    throw new Exception("Expected equal-height aligned charts with automatic sub-millisecond ranges and retained spike ranges.");
                if (layout && (analysis!.ActualWidth > settings!.ActualWidth * .6 || view.ActualWidth < 100
                    || settings.AudioColumn.TransformToVisual(settings).TransformPoint(default).Y < analysis.ActualHeight))
                    throw new Exception("Expected audio/video views above their respective control columns.");
                var cpu = (process.TotalProcessorTime - initialCpu).TotalSeconds / seconds;
                profile.Describe("Client pixels", $"{window.AppWindow.ClientSize.Width}x{window.AppWindow.ClientSize.Height}");
                profile.Describe("Chart DIPs", $"{view.ActualWidth:0}x{view.ActualHeight:0}");
                profile.Describe("Rasterization scale", view.XamlRoot.RasterizationScale.ToString());
                profile.Describe("Library", "Direct WriteableBitmap CPU raster, opaque centers + quarter-opacity cross halos, all raw samples");
                profile.Describe("Workload", $"{windowSeconds} seconds; synthetic 60 Hz frames and 1000 Hz analysis/handoff with periodic spikes; chart updates at {refreshRate} Hz");
                profile.Describe("Binning", "None");
                profile.Describe("Points", view.PointCount.ToString());
                profile.Describe("Plotted points", view.PlottedPointCount.ToString());
                profile.Describe("Completed chart updates/s", ((view.CompletedUpdates - initialUpdates) / seconds).ToString("F2"));
                profile.Describe("CPU core equivalents", cpu.ToString("F3"));
                profile.Describe("Private MiB", (process.PrivateMemorySize64 / 1048576.0).ToString("F1"));
                profile.Describe("Metric", "chart.frame_time_update_ms = synchronous preparation + raster + bitmap upload; excludes compositor/presentation");
                profile.Save(output);
                // Render the actual XAML tree for visual review, without desktop capture.
                var image = new RenderTargetBitmap();
                await image.RenderAsync((UIElement)window.Content);
                var pixels = await image.GetPixelsAsync();
                var folder = await StorageFolder.GetFolderFromPathAsync(output);
                var file = await folder.CreateFileAsync(layout ? "layout.png" : $"chart-{windowSeconds}s.png", CreationCollisionOption.ReplaceExisting);
                using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    (uint)image.PixelWidth, (uint)image.PixelHeight, 96, 96, pixels.ToArray());
                await encoder.FlushAsync();
                view.Attach(new PipelineProfile());
                if (view.PlottedPointCount != 0 || view.LostSamples != 0
                    || view.Processing.LowerLimit != null || view.Processing.UpperLimit != null
                    || view.Scheduling.LowerLimit != null || view.Scheduling.UpperLimit != null
                    || view.Processing.PresentTime != 0 || view.Scheduling.PresentTime != 0)
                    throw new Exception("Profile replacement must reset both charts and their expanded ranges.");
                window.Close();
                Exit();
            }
            catch (Exception error)
            {
                finishing = true;
                timer.Stop();
                File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString());
                Environment.ExitCode = 1;
                window.Close();
                Exit();
            }
        };
        view.Loaded += (_, _) => timer.Start();
        window.Activate();
    }
}


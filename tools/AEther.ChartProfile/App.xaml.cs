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
    readonly string output = Path.GetFullPath("artifacts/chart-profile");

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
        Directory.CreateDirectory(output);
        var arguments = Environment.GetCommandLineArgs();
        var windowSeconds = FrameTimeView.HistorySeconds;
        var profile = new PipelineProfile();
        window = new Window { Title = "AEther LiveCharts2 frame-time stress probe" };
        var view = new FrameTimeView(window);
        var refreshRate = arguments.Contains("--fast") ? 60 : 1 / view.RefreshInterval.TotalSeconds;
        view.RefreshInterval = TimeSpan.FromSeconds(1.0 / refreshRate);
        var layout = arguments.Contains("--layout");
        AnalysisView? analysis = null;
        SettingsPanel? settings = null;
        if (layout)
        {
            analysis = new AnalysisView(window);
            settings = new SettingsPanel(new AudioPanel(), new ShaderPanel(), new DebugPanel(), analysis, view);
        }
        window.Content = (UIElement?)settings ?? view;
        // Both the chart-only and layout probes use at least 1280x720 client pixels.
        window.AppWindow.ResizeClient(new SizeInt32(1280, 720));
        view.Attach(profile);
        const double inputRate = 240;
        long generated = 0;
        var origin = Stopwatch.GetTimestamp() - (long)(windowSeconds * Stopwatch.Frequency);
        void GenerateUntil(long timestamp)
        {
            while (origin + (long)(generated * Stopwatch.Frequency / inputRate) <= timestamp)
            {
                var time = origin + (long)(generated * Stopwatch.Frequency / inputRate);
                var ms = 1000 / inputRate + .3 * Math.Sin(generated * .07) + (generated % 997 == 0 ? 12 : 0);
                profile.Record("render.frame_interval_ms", ms, time);
                // Emulate delayed GPU results, including reversed completion order within each pair.
                if (generated % 2 == 1)
                {
                    profile.Record("gpu.frame_ms", 1.4 + .2 * Math.Sin(generated * .05), time);
                    profile.Record("gpu.frame_ms", 1.4 + .2 * Math.Sin((generated - 1) * .05),
                        origin + (long)((generated - 1) * Stopwatch.Frequency / inputRate));
                }
                profile.Record("render.total_audio_age_ms", 24 + 2 * Math.Sin(generated * .003), time);
                generated++;
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
                if (view.PointCount < windowSeconds * inputRate - 2 || view.LostSamples != 0 || view.CompletedUpdates - initialUpdates < Math.Max(1, 5 * refreshRate))
                    throw new Exception($"Chart stalled or lost samples: {view.PointCount} points, {view.CompletedUpdates} draws, {view.LostSamples} lost.");
                if (view.GpuPointCount < windowSeconds * inputRate - 3 || view.AgePointCount < windowSeconds * inputRate - 2)
                    throw new Exception($"Missing additional traces: {view.GpuPointCount} GPU points, {view.AgePointCount} age points.");
                if (layout && (analysis!.ActualWidth > settings!.ActualWidth * .6 || view.ActualWidth < 100
                    || settings.AudioColumn.TransformToVisual(settings).TransformPoint(default).Y < analysis.ActualHeight))
                    throw new Exception("Expected audio/video views above their respective control columns.");
                var cpu = (process.TotalProcessorTime - initialCpu).TotalSeconds / seconds;
                profile.Describe("Client pixels", $"{window.AppWindow.ClientSize.Width}x{window.AppWindow.ClientSize.Height}");
                profile.Describe("Chart DIPs", $"{view.ActualWidth:0}x{view.ActualHeight:0}");
                profile.Describe("Rasterization scale", view.XamlRoot.RasterizationScale.ToString());
                profile.Describe("Library", "LiveCharts2 2.0.4, default CPU Skia rendering");
                profile.Describe("Workload", $"{windowSeconds} seconds at 240 samples/s; no averaging, animations or point markers; requested chart updates at {refreshRate} Hz");
                profile.Describe("Points", view.PointCount.ToString());
                profile.Describe("GPU points", view.GpuPointCount.ToString());
                profile.Describe("Age points", view.AgePointCount.ToString());
                profile.Describe("Completed chart updates/s", ((view.CompletedUpdates - initialUpdates) / seconds).ToString("F2"));
                profile.Describe("CPU core equivalents", cpu.ToString("F3"));
                profile.Describe("Private MiB", (process.PrivateMemorySize64 / 1048576.0).ToString("F1"));
                profile.Describe("Metric", "chart.frame_time_update_ms = wall time from Measuring to UpdateFinished, includes scheduling; not GPU time");
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

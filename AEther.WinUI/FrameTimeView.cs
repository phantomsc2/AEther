using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace AEther.WinUI;

/// <summary>Native-resolution processing and scheduling timings on aligned timelines.</summary>
public sealed class FrameTimeView : UserControl
{
    internal const int HistorySeconds = 7;
    readonly TimingChart processing = new(
    [
        ("analysis.processing_ms", "Analysis", Color.FromArgb(255, 70, 185, 140), "One batch from processing entry through completed output preparation, before subscriber callbacks."),
        ("render.draw_cpu_ms", "Render", Color.FromArgb(255, 170, 115, 225), "Frame preparation and draw submission before Present."),
        ("gpu.frame_ms", "GPU", Color.FromArgb(255, 235, 170, 60), "Execution measured by GPU timestamps, placed at the original frame time."),
    ]);
    readonly TimingChart scheduling = new(
    [
        ("render.frame_interval_ms", "Frame", Color.FromArgb(255, 70, 160, 230), "Interval between render starts."),
        ("capture.queue_ms", "Queue", Color.FromArgb(255, 70, 185, 140), "Capture callback handoff entry through input copying and queue wait to analyzer dequeue, once per packet."),
        ("render.analysis_handoff_ms", "Handoff", Color.FromArgb(255, 170, 115, 225), "Analysis completion through callback copying and handoff wait to renderer history acceptance, once per accepted live row, before presentation scheduling."),
        ("render.pacing_wait_ms", "Pacing", Color.FromArgb(255, 235, 170, 60), "Render-worker wait for compositor timing and swap-chain readiness."),
    ]);
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1.0 / 10) };
    readonly Window owner;
    PipelineProfile? profile;
    long origin;
    internal int PointCount => scheduling.PointCount;
    internal int RenderPointCount => processing.PointCounts[1];
    internal int GpuPointCount => processing.PointCounts[2];
    internal int AnalysisPointCount => processing.PointCounts[0];
    internal int[] SchedulingPointCounts => scheduling.PointCounts;
    internal string[] CurveNames => processing.CurveNames.Concat(scheduling.CurveNames).ToArray();
    internal int PlottedPointCount => processing.PlottedPointCount + scheduling.PlottedPointCount;
    internal long LostSamples => processing.LostSamples + scheduling.LostSamples;
    internal long CompletedUpdates => processing.CompletedUpdates + scheduling.CompletedUpdates;
    internal long UpdateCount => processing.UpdateCount + scheduling.UpdateCount;
    internal TimeSpan RefreshInterval { get => refresh.Interval; set => refresh.Interval = value < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : value; }

    internal TimingChart Processing => processing;
    internal TimingChart Scheduling => scheduling;

    public FrameTimeView(Window owner)
    {
        this.owner = owner;
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        AddPanel(processing, "Processing", 0);
        AddPanel(scheduling, "Scheduling", 1);
        Content = grid;
        refresh.Tick += (_, _) =>
        {
            if (!owner.AppWindow.IsVisible || owner.AppWindow.Presenter is OverlappedPresenter
                { State: OverlappedPresenterState.Minimized } || ActualHeight < 128 || ActualWidth < 64) return;
            double now = Stopwatch.GetElapsedTime(origin).TotalSeconds;
            processing.Refresh(now);
            scheduling.Refresh(now);
        };
        Loaded += (_, _) => refresh.Start();
        Unloaded += (_, _) => refresh.Stop();

        void AddPanel(TimingChart chart, string title, int row)
        {
            var panel = new Grid();
            panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(8, 2, 0, 0) });
            Grid.SetRow(chart, 1);
            panel.Children.Add(chart);
            Grid.SetRow(panel, row);
            grid.Children.Add(panel);
        }
    }

    public void Attach(PipelineProfile? next)
    {
        if (ReferenceEquals(profile, next)) return;
        profile = next;
        origin = Stopwatch.GetTimestamp();
        processing.Attach(next, origin);
        scheduling.Attach(next, origin);
    }
}

using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace AEther.WinUI;

/// <summary>Analysis, rendering, and frame interval timings on a shared timeline.</summary>
public sealed class FrameTimeView : UserControl
{
    internal const int HistorySeconds = 7;
    readonly TimingChart chart = new("Frame timings",
    [
        ("analysis.processing_ms", "Analysis", Color.FromArgb(255, 70, 185, 140), "One batch from processing entry through completed output preparation, before subscriber callbacks."),
        ("render.draw_cpu_ms", "Render", Color.FromArgb(255, 170, 115, 225), "Frame preparation and draw submission before Present."),
        ("gpu.frame_ms", "GPU", Color.FromArgb(255, 235, 170, 60), "Execution measured by GPU timestamps, placed at the original frame time."),
        ("render.frame_interval_ms", "Frame", Color.FromArgb(255, 70, 160, 230), "Interval between render starts."),
    ]);
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1.0 / 10) };
    readonly Window owner;
    PipelineProfile? profile;
    long origin;
    double nextBudgetCheck;
    bool canvasActive;
    internal Func<double?>? FrameBudgetProvider { get; set; }
    internal int PointCount => chart.PointCounts[3];
    internal int RenderPointCount => chart.PointCounts[1];
    internal int GpuPointCount => chart.PointCounts[2];
    internal int AnalysisPointCount => chart.PointCounts[0];
    internal string[] CurveNames => chart.CurveNames;
    internal int PlottedPointCount => chart.PlottedPointCount;
    internal long LostSamples => chart.LostSamples;
    internal long CompletedUpdates => chart.CompletedUpdates;
    internal long UpdateCount => chart.UpdateCount;
    internal TimeSpan RefreshInterval { get => refresh.Interval; set => refresh.Interval = value < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : value; }

    internal TimingChart Chart => chart;

    public FrameTimeView(Window owner)
    {
        this.owner = owner;
        Content = chart;
        refresh.Tick += (_, _) =>
        {
            if (!owner.AppWindow.IsVisible || owner.AppWindow.Presenter is OverlappedPresenter
                { State: OverlappedPresenterState.Minimized } || ActualHeight < 128 || ActualWidth < 64) return;
            double now = Stopwatch.GetElapsedTime(origin).TotalSeconds;
            if (canvasActive && now >= nextBudgetCheck)
            {
                chart.SetFrameBudget(FrameBudgetProvider?.Invoke());
                nextBudgetCheck = now + 1;
            }
            chart.Refresh(now);
        };
        Loaded += (_, _) => refresh.Start();
        Unloaded += (_, _) => refresh.Stop();
    }

    internal void SetCanvasActive(bool active)
    {
        if (canvasActive == active) return;
        canvasActive = active;
        nextBudgetCheck = 0;
        if (!active) chart.SetFrameBudget(null);
    }

    public void Attach(PipelineProfile? next)
    {
        if (ReferenceEquals(profile, next)) return;
        profile = next;
        origin = Stopwatch.GetTimestamp();
        nextBudgetCheck = 0;
        chart.SetFrameBudget(null);
        chart.Attach(next, origin);
    }
}

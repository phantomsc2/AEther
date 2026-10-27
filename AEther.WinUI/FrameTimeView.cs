using System.Diagnostics;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.WinUI;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SkiaSharp;

namespace AEther.WinUI;

/// <summary>Per-frame intervals, GPU durations and estimated source-to-presentation ages, without decimation.</summary>
public sealed class FrameTimeView : UserControl
{
    const string FrameMetric = "render.frame_interval_ms";
    const int Capacity = 32768;
    internal const int HistorySeconds = 10;
    readonly CartesianChart chart = new()
    {
        AnimationsSpeed = TimeSpan.Zero,
        AutoUpdateEnabled = false,
        UpdaterThrottler = TimeSpan.Zero,
        TooltipPosition = TooltipPosition.Hidden,
        LegendPosition = LegendPosition.Top,
    };
    readonly Trace[] traces =
    [
        new(FrameMetric, "Frame time", new SKColor(70, 160, 230)),
        new("gpu.frame_ms", "GPU", new SKColor(235, 170, 60)),
        new("render.total_audio_age_ms", "Total age", new SKColor(170, 115, 225)),
    ];
    readonly Axis timeAxis = new() { TextSize = 11, MinStep = 1 };
    readonly LogarithmicAxis durationAxis = new(10)
    {
        TextSize = 11,
        // The chart plots log10(milliseconds), so tick positions use the same scale.
        CustomSeparators = Enumerable.Range(-6, 16)
            .SelectMany(exponent => new[] { (double)exponent, exponent + Math.Log10(2), exponent + Math.Log10(5) })
            .ToArray(),
        Labeler = value => Math.Pow(10, value).ToString("G3"),
    };
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Window owner;
    PipelineProfile? profile;
    long origin, lost, measureStart;
    double latestTime;
    bool dirty;

    internal int PointCount => traces[0].Points.Count;
    internal int GpuPointCount => traces[1].Points.Count;
    internal int AgePointCount => traces[2].Points.Count;
    internal long UpdateCount { get; private set; }
    internal long LostSamples => lost;
    internal TimeSpan RefreshInterval { get => refresh.Interval; set => refresh.Interval = value; }

    public FrameTimeView(Window owner)
    {
        this.owner = owner;
        chart.Series = traces.Select(trace => trace.Series).ToArray();
        timeAxis.Labeler = value => $"{value - latestTime:0}s";
        chart.XAxes = [timeAxis];
        chart.YAxes = [durationAxis];
        timeAxis.ShowSeparatorLines = false;
        durationAxis.SeparatorsPaint = new SolidColorPaint(new SKColor(128, 128, 128, 90), 1);
        chart.Measuring += _ => Interlocked.Exchange(ref measureStart, Stopwatch.GetTimestamp());
        chart.UpdateFinished += _ =>
        {
            var start = Interlocked.Exchange(ref measureStart, 0);
            if (start != 0) profile?.Elapsed("chart.frame_time_update_ms", start);
            Interlocked.Increment(ref completedUpdates);
        };
        ToolTipService.SetToolTip(chart, "Frame interval, GPU execution time, and audio source age at estimated presentation. Total age requires source timestamps and a presentation estimate; it is not calibrated audio-to-photon latency.");

        Content = chart;
        refresh.Tick += (_, _) => Refresh();
        SizeChanged += (_, _) => dirty = true;
        ActualThemeChanged += (_, _) => dirty = true;
        Loaded += (_, _) => refresh.Start();
        Unloaded += (_, _) => refresh.Stop();
    }

    long completedUpdates;
    internal long CompletedUpdates => Interlocked.Read(ref completedUpdates);

    public void Attach(PipelineProfile? next)
    {
        if (ReferenceEquals(profile, next)) return;
        profile = next;
        origin = Stopwatch.GetTimestamp();
        lost = 0;
        Interlocked.Exchange(ref measureStart, 0);
        latestTime = 0;
        lock (chart.SyncContext)
            foreach (var trace in traces)
            {
                trace.History = next?.Track(trace.Metric, Capacity);
                trace.Cursor = 0;
                trace.Points.Clear();
            }
        dirty = true;
    }

    void Refresh()
    {
        if (!owner.AppWindow.IsVisible || owner.AppWindow.Presenter is OverlappedPresenter
            { State: OverlappedPresenterState.Minimized } || ActualHeight < 64 || ActualWidth < 64) return;
        bool received = false;
        foreach (var trace in traces)
        {
            if (trace.History != null) lost += trace.History.ReadSince(ref trace.Cursor, trace.Incoming);
            else trace.Incoming.Clear();
            received |= trace.Incoming.Count != 0;
        }
        if (!received && !dirty) return;
        lock (chart.SyncContext)
        {
            foreach (var trace in traces)
            {
                bool unordered = false;
                foreach (var sample in trace.Incoming)
                {
                    if (!double.IsFinite(sample.Milliseconds) || sample.Milliseconds <= 0) continue;
                    var time = Stopwatch.GetElapsedTime(origin, sample.Timestamp).TotalSeconds;
                    latestTime = Math.Max(latestTime, time);
                    unordered |= trace.Points.Count > 0 && time < trace.Points[^1].Time;
                    trace.Points.Add(new(time, sample.Milliseconds));
                }
                if (unordered) trace.Points.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            foreach (var trace in traces)
            {
                var expired = 0;
                while (expired < trace.Points.Count && trace.Points[expired].Time < latestTime - HistorySeconds) expired++;
                expired = Math.Max(expired, trace.Points.Count - Capacity);
                if (expired > 0) trace.Points.RemoveRange(0, expired);
            }
            timeAxis.MinLimit = latestTime - HistorySeconds;
            timeAxis.MaxLimit = latestTime;
        }
        ((IChartView)chart).CoreChart.Update(new ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
        UpdateCount++;
        dirty = false;
    }

    sealed class Trace
    {
        public string Metric { get; }
        public List<FramePoint> Points { get; } = new(Capacity);
        public List<TimingSample> Incoming { get; } = new();
        public LineSeries<FramePoint> Series { get; }
        public TimingHistory? History;
        public long Cursor;

        public Trace(string metric, string name, SKColor color)
        {
            Metric = metric;
            Series = new()
            {
                Name = name,
                Values = Points,
                Stroke = new SolidColorPaint(color, 1),
                GeometrySize = 0,
                GeometryStroke = null,
                GeometryFill = null,
                Fill = null,
                EnableNullSplitting = false,
                IsHoverable = false,
                DataPadding = new(0, 0),
            };
        }
    }

    // Native chart entities cache their coordinates/metadata instead of using the library's per-update mapper lookup.
    sealed class FramePoint(double time, double milliseconds) : IChartEntity
    {
        public double Time { get; } = time;
        public Coordinate Coordinate { get; } = new(time, Math.Log10(milliseconds));
        public ChartEntityMetaData? MetaData { get; set; }
    }
}

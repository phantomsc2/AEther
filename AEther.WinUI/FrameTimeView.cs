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

/// <summary>Native-resolution processing and scheduling timings on aligned timelines.</summary>
public sealed class FrameTimeView : UserControl
{
    internal const int HistorySeconds = 7;
    readonly TimingChart processing = new(
    [
        ("analysis.processing_ms", "Analysis", new SKColor(70, 185, 140), "One batch from processing entry through completed output preparation, before subscriber callbacks."),
        ("render.draw_cpu_ms", "Render", new SKColor(170, 115, 225), "Frame preparation and draw submission before Present."),
        ("gpu.frame_ms", "GPU", new SKColor(235, 170, 60), "Execution measured by GPU timestamps, placed at the original frame time."),
    ]);
    readonly TimingChart scheduling = new(
    [
        ("render.frame_interval_ms", "Frame", new SKColor(70, 160, 230), "Interval between render starts."),
        ("capture.queue_ms", "Queue", new SKColor(70, 185, 140), "Capture callback handoff entry through input copying and queue wait to analyzer dequeue, once per packet."),
        ("render.analysis_handoff_ms", "Handoff", new SKColor(170, 115, 225), "Analysis completion through callback copying and handoff wait to renderer history acceptance, once per accepted live row, before presentation scheduling."),
        ("render.pacing_wait_ms", "Pacing", new SKColor(235, 170, 60), "Render-worker wait for compositor timing and swap-chain readiness."),
    ]);
    readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1.0 / 5) };
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
    internal TimeSpan RefreshInterval { get => refresh.Interval; set => refresh.Interval = value; }
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
/// <summary>Native metric samples on one wrapping timeline, without decimation.</summary>
internal sealed class TimingChart : UserControl
{
    const int Capacity = 32768;
    const int HistorySeconds = FrameTimeView.HistorySeconds;
    static readonly double[] LogTicks = Enumerable.Range(-6, 16)
        .SelectMany(exponent => new[] { (double)exponent, exponent + Math.Log10(2), exponent + Math.Log10(5) })
        .ToArray();
    readonly CartesianChart chart = new()
    {
        AnimationsSpeed = TimeSpan.Zero,
        AutoUpdateEnabled = false,
        UpdaterThrottler = TimeSpan.Zero,
        TooltipPosition = TooltipPosition.Hidden,
        LegendPosition = LegendPosition.Hidden,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    readonly Trace[] traces;
    readonly Axis timeAxis = new()
    {
        TextSize = 11, MinStep = 1,
        MinLimit = 0, MaxLimit = HistorySeconds,
        Labeler = value => $"{value:0}s",
    };
    readonly RectangularSection present = new()
    {
        Xi = 0, Xj = 0,
        Stroke = new SolidColorPaint(new SKColor(160, 160, 160, 200), 1),
        Fill = null,
    };
    readonly Axis durationAxis = new()
    {
        TextSize = 11,
        // The chart plots log10(milliseconds), so tick positions use the same scale.
        CustomSeparators = Array.Empty<double>(),
        Labeler = value => Math.Pow(10, value).ToString("G3"),
    };
    PipelineProfile? profile;
    long origin, lost, measureStart;
    double latestTime;
    bool dirty;

    internal int PointCount => traces[0].Points.Count;
    internal int PlottedPointCount => traces.Sum(trace => trace.Points.Count);
    internal int[] PointCounts => traces.Select(trace => trace.Points.Count).ToArray();
    internal string[] CurveNames => traces.Select(trace => trace.Series.Name!).ToArray();
    internal double? LowerLimit => durationAxis.MinLimit;
    internal double? UpperLimit => durationAxis.MaxLimit;
    internal double PresentTime => present.Xi ?? 0;
    internal long LostSamples => lost;
    internal long UpdateCount { get; private set; }

    internal TimingChart((string Metric, string Name, SKColor Color, string Description)[] definitions)
    {
        traces = definitions.Select(item => new Trace(item.Metric, item.Name, item.Color)).ToArray();
        // Fixed drawing margins keep the two timelines aligned as their Y ranges differ.
        chart.DrawMargin = new LiveChartsCore.Measure.Margin(65, 10, 15, 25);
        chart.Series = traces.Select(trace => trace.Series).ToArray();
        chart.Sections = [present];
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
        var tooltip = new StackPanel { MaxWidth = 320, Spacing = 6 };
        foreach (var item in definitions) AddTooltipRow(item.Name, item.Description);
        ToolTipService.SetToolTip(chart, tooltip);

        void AddTooltipRow(string label, string description)
        {
            var row = new TextBlock { TextWrapping = TextWrapping.Wrap };
            row.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
            row.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " – " + description });
            tooltip.Children.Add(row);
        }
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
        layout.Children.Add(chart);
        var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        foreach (var item in definitions)
        {
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            entry.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(item.Color.Alpha, item.Color.Red, item.Color.Green, item.Color.Blue)),
            });
            entry.Children.Add(new TextBlock { Text = item.Name, FontSize = 11 });
            legend.Children.Add(entry);
        }
        Grid.SetColumn(legend, 1);
        layout.Children.Add(legend);
        Content = layout;
        SizeChanged += (_, _) => dirty = true;
        ActualThemeChanged += (_, _) => dirty = true;
    }
    long completedUpdates;
    internal long CompletedUpdates => Interlocked.Read(ref completedUpdates);

    internal void Attach(PipelineProfile? next, long timestamp)
    {
        if (ReferenceEquals(profile, next)) return;
        profile = next;
        origin = timestamp;
        lost = 0;
        Interlocked.Exchange(ref measureStart, 0);
        latestTime = 0;
        lock (chart.SyncContext)
        {
            present.Xi = present.Xj = 0;
            durationAxis.MinLimit = null;
            durationAxis.MaxLimit = null;
            foreach (var trace in traces)
            {
                trace.History = next?.Track(trace.Metric, Capacity);
                trace.Cursor = 0;
                trace.Points.Clear();
            }
        }
        dirty = true;
    }

    internal void Refresh(double now)
    {
        bool received = false;
        foreach (var trace in traces)
        {
            if (trace.History != null) lost += trace.History.ReadSince(ref trace.Cursor, trace.Incoming);
            else trace.Incoming.Clear();
            received |= trace.Incoming.Count != 0;
        }
        if (!received && !dirty && traces.All(trace => trace.Points.Count == 0)) return;
        lock (chart.SyncContext)
        {
            foreach (var trace in traces)
            {
                bool unordered = false;
                foreach (var sample in trace.Incoming)
                {
                    if (!double.IsFinite(sample.Milliseconds) || sample.Milliseconds <= 0) continue;
                    var time = Stopwatch.GetElapsedTime(origin, sample.Timestamp).TotalSeconds;
                    unordered |= trace.Points.Count > 0 && time < trace.Points[^1].Time;
                    trace.Points.Add(new(time, sample.Milliseconds));
                }
                if (unordered) trace.Points.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            latestTime = now;
            foreach (var trace in traces)
            {
                var expired = 0;
                while (expired < trace.Points.Count && trace.Points[expired].Time < latestTime - HistorySeconds) expired++;
                expired = Math.Max(expired, trace.Points.Count - Capacity);
                if (expired > 0) trace.Points.RemoveRange(0, expired);
            }
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var trace in traces)
                foreach (var point in trace.Points)
                {
                    min = Math.Min(min, point.LogMilliseconds);
                    max = Math.Max(max, point.LogMilliseconds);
                }
            if (double.IsFinite(min))
            {
                // LiveCharts creates/measures custom tick visuals even outside the visible range.
                // Keep only the useful 1-2-5 ticks, rather than all sixteen decades.
                // Retain the established range when samples expire, avoiding vertical jitter.
                if (durationAxis.MinLimit is double oldMin) min = Math.Min(min, oldMin);
                if (durationAxis.MaxLimit is double oldMax) max = Math.Max(max, oldMax);
                var lower = LogTicks.LastOrDefault(tick => tick <= min, Math.Floor(min));
                var upper = LogTicks.FirstOrDefault(tick => tick >= max, Math.Ceiling(max));
                if (upper <= lower)
                    upper = LogTicks.FirstOrDefault(tick => tick > lower, lower + 1);
                var ticks = LogTicks.Where(tick => tick >= lower && tick <= upper).ToArray();
                // Two half-height charts can span many decades. Thin axis labels,
                // never metric samples, to keep the logarithmic scale readable.
                int tickBudget = Math.Max(2, (int)((ActualHeight - 65) / 18));
                if (ticks.Length > tickBudget)
                {
                    int step = (int)Math.Ceiling((ticks.Length - 1.0) / (tickBudget - 1));
                    double last = ticks[^1];
                    ticks = ticks.Where((_, index) => index % step == 0).Append(last).Distinct().ToArray();
                }
                if (!ticks.SequenceEqual(durationAxis.CustomSeparators ?? [])) durationAxis.CustomSeparators = ticks;
                durationAxis.MinLimit = lower;
                durationAxis.MaxLimit = upper;
            }
            present.Xi = present.Xj = WrapTime(latestTime);
        }
        ((IChartView)chart).CoreChart.Update(new ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
        UpdateCount++;
        dirty = false;
    }

    // Keep absolute timestamps for expiry and ordering; only plotted coordinates wrap.
    static double WrapTime(double time) => (time % HistorySeconds + HistorySeconds) % HistorySeconds;

    sealed class Trace
    {
        public string Metric { get; }
        public List<FramePoint> Points { get; } = new(Capacity);
        public List<TimingSample> Incoming { get; } = new();
        public ScatterSeries<FramePoint> Series { get; }
        public TimingHistory? History;
        public long Cursor;

        public Trace(string metric, string name, SKColor color)
        {
            Metric = metric;
            Series = new()
            {
                Name = name,
                Values = Points,
                Stroke = null,
                GeometrySize = 3,
                Fill = new SolidColorPaint(color),
                IsHoverable = false,
                DataPadding = new(0, 0),
            };
        }
    }

    // Native chart entities cache their coordinates/metadata instead of using the library's per-update mapper lookup.
    sealed class FramePoint : IChartEntity
    {
        public double Time { get; }
        public double LogMilliseconds { get; }
        public Coordinate Coordinate { get; }
        public ChartEntityMetaData? MetaData { get; set; }

        public FramePoint(double time, double milliseconds)
        {
            Time = time;
            LogMilliseconds = Math.Log10(milliseconds);
            Coordinate = new(WrapTime(time), LogMilliseconds);
        }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace AEther.WinUI;

/// <summary>Soft five-pixel scatter kernels; full raw history, no chart entities or decimation.</summary>
internal sealed class TimingChart : UserControl
{
    const int Capacity = 32768;
    const int HistorySeconds = FrameTimeView.HistorySeconds;
    readonly Trace[] traces;
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly Canvas yLabels = new();
    readonly Canvas secondBars = new() { IsHitTestVisible = false };
    readonly Grid plot = new();
    readonly string metric;
    readonly TimingScale scaleBounds = new();
    WriteableBitmap? bitmap;
    int[] pixels = [];
    ScatterCache? cache;
    PipelineProfile? profile;
    long origin, lost;
    bool dirty = true;
    double latestTime;
    double[] ticks = [];
    internal int PointCount => traces[0].Points.Count;
    internal int PlottedPointCount => traces.Sum(t => t.Points.Count);
    internal int[] PointCounts => traces.Select(t => t.Points.Count).ToArray();
    internal string[] CurveNames => traces.Select(t => t.Name).ToArray();
    internal double? LowerLimit { get; private set; }
    internal double? UpperLimit { get; private set; }
    internal double PresentTime => ScatterRaster.Wrap(latestTime, HistorySeconds);
    internal long LostSamples => lost;
    internal long UpdateCount { get; private set; }
    internal long CompletedUpdates { get; private set; }

    internal TimingChart(string title, (string Metric, string Name, Color Color, string Description)[] definitions)
    {
        traces = definitions.Select(d => new Trace(d.Metric, d.Name, d.Color)).ToArray();
        metric = definitions[0].Name == "Analysis" ? "chart.processing_update_ms" : "chart.scheduling_update_ms";
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(60) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });

        layout.Children.Add(yLabels);
        plot.Children.Add(secondBars);
        plot.Children.Add(image);
        Grid.SetColumn(plot, 1);
        layout.Children.Add(plot);
        var legend = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        legend.Children.Add(new TextBlock { Text = title, TextAlignment = TextAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var tokens = new TokenView { SelectionMode = ListViewSelectionMode.Multiple, IsWrapped = true,
            CanRemoveTokens = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int i = 0; i < definitions.Length; i++)
        {
            var d = definitions[i];
            var label = new TextBlock { Text = d.Name, FontSize = 11 };
            var token = new TokenItem { Content = label, Tag = i, Width = 88, IsRemoveable = false };
            var fill = new SolidColorBrush(d.Color);
            token.Resources["TokenItemBackgroundSelected"] = fill;
            token.Resources["TokenItemBackgroundPointerOverSelected"] = fill;
            token.Resources["TokenItemBackgroundPressedSelected"] = fill;
            token.Resources["TokenItemBorderBrushSelected"] = fill;
            token.Resources["TokenItemBorderBrushPointerOverSelected"] = fill;
            token.Resources["TokenItemBorderBrushPressedSelected"] = fill;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(token, d.Name);
            ToolTipService.SetToolTip(token, d.Description);
            tokens.Items.Add(token);
            tokens.SelectedItems.Add(token);
        }
        tokens.SelectionChanged += (_, e) =>
        {
            foreach (TokenItem token in e.RemovedItems) SetVisible((int)token.Tag, false);
            foreach (TokenItem token in e.AddedItems) SetVisible((int)token.Tag, true);
        };
        legend.Children.Add(tokens);
        Grid.SetColumn(legend, 2);
        layout.Children.Add(legend);
        Content = layout;
        plot.SizeChanged += (_, _) => { dirty = true; UpdateLabels(); };
        ActualThemeChanged += (_, _) => dirty = true;
    }

    void SetVisible(int index, bool visible)
    {
        traces[index].Visible = visible;
        if (cache == null) return;
        cache.SetVisible(index, visible);
        if (!dirty) Render();
    }

    internal void Attach(PipelineProfile? next, long timestamp)
    {
        if (ReferenceEquals(profile, next)) return;
        profile = next;
        origin = timestamp;
        lost = 0;
        latestTime = 0;
        LowerLimit = UpperLimit = null;
        scaleBounds.Reset();
        ticks = [];
        foreach (var trace in traces)
        {
            trace.History = next?.Track(trace.Metric, Capacity);
            trace.Cursor = 0;
            trace.Points.Clear();
        }
        dirty = true;
        UpdateLabels();
    }

    internal void Refresh(double now)
    {
        long start = Stopwatch.GetTimestamp();
        bool received = false;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int traceIndex = 0; traceIndex < traces.Length; traceIndex++)
        {
            var trace = traces[traceIndex];
            if (trace.History != null) lost += trace.History.ReadSince(ref trace.Cursor, trace.Incoming);
            else trace.Incoming.Clear();
            received |= trace.Incoming.Count != 0;
            bool unordered = false;
            foreach (var sample in trace.Incoming)
            {
                if (!double.IsFinite(sample.Milliseconds) || sample.Milliseconds <= 0) continue;
                double time = Stopwatch.GetElapsedTime(origin, sample.Timestamp).TotalSeconds;
                unordered |= trace.Points.Count > 0 && time < trace.Points[^1].Time;
                var point = new ScatterPoint(time, Math.Log10(sample.Milliseconds));
                trace.Points.Add(point);
                cache?.Add(traceIndex, point);

            }
            if (unordered) trace.Points.Sort((a, b) => a.Time.CompareTo(b.Time));
            int expired = 0;
            while (expired < trace.Points.Count && trace.Points[expired].Time < now - HistorySeconds) expired++;
            expired = Math.Max(expired, trace.Points.Count - Capacity);
            if (expired > 0)
            {
                received = true; // Upload the cleared cache even when the last sample expires.
                for (int i = 0; i < expired; i++) cache?.Remove(traceIndex, trace.Points[i]);
                trace.Points.RemoveRange(0, expired);
            }
        }
        foreach (var trace in traces)
            foreach (var point in trace.Points)
            {
                min = Math.Min(min, point.LogMilliseconds);
                max = Math.Max(max, point.LogMilliseconds);
            }
        if (!received && !dirty && traces.All(t => t.Points.Count == 0)) return;
        latestTime = now;
        if (scaleBounds.Update(min, max, now))
        {
            LowerLimit = scaleBounds.Lower; UpperLimit = scaleBounds.Upper;
            dirty = true;
            ticks = TimingScale.Ticks(LowerLimit!.Value, UpperLimit!.Value);
            UpdateLabels();
        }
        profile?.Elapsed(metric.Replace("_update_ms", "_prepare_ms"), start);
        long rasterStart = Stopwatch.GetTimestamp();
        Render();
        profile?.Elapsed(metric.Replace("_update_ms", "_raster_upload_ms"), rasterStart);
        profile?.Elapsed(metric, start);
        profile?.Elapsed("chart.frame_time_update_ms", start);
        UpdateCount++;
        CompletedUpdates++;
        dirty = false;
    }

    void UpdateLabels()
    {
        yLabels.Children.Clear();
        secondBars.Children.Clear();
        double height = plot.ActualHeight, width = plot.ActualWidth;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        if (LowerLimit is double lower && UpperLimit is double upper)
        {
            int step = Math.Max(1, (int)Math.Ceiling(ticks.Length / Math.Max(2, height / 18)));
            for (int i = 0; i < ticks.Length; i += step)
            {
                var label = new TextBlock { Text = Math.Pow(10, ticks[i]).ToString("G3", CultureInfo.CurrentCulture), FontSize = 11, Width = 60, TextAlignment = TextAlignment.Center };
                Canvas.SetTop(label, Math.Clamp((upper - ticks[i]) / (upper - lower) * height - 7, 0, Math.Max(0, height - 14)));
                Canvas.SetLeft(label, 0);
                yLabels.Children.Add(label);
            }
        }
        for (int i = 0; i <= HistorySeconds; i++)
        {
            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 1 / scale, Height = height,
                Fill = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
            };
            Canvas.SetLeft(bar, Math.Floor(Math.Max(0, Math.Ceiling(width * scale) - 1) * i / HistorySeconds) / scale);
            secondBars.Children.Add(bar);
        }
    }

    void Render()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        int width = Math.Max(1, (int)Math.Ceiling(plot.ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(plot.ActualHeight * scale));
        if (bitmap == null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = new(width, height);
            pixels = new int[checked(width * height)];
            image.Source = bitmap;
            cache = null;
            UpdateLabels();
        }
        if (dirty || cache == null)
        {
            if (cache == null)
            {
                cache = new ScatterCache(width, height, LowerLimit ?? -3, UpperLimit ?? 2,
                    HistorySeconds, traces.Select(t => t.Color).ToArray(), ticks);
                for (int i = 0; i < traces.Length; i++)
                    if (!traces[i].Visible) cache.SetVisible(i, false);
            }
            else cache.Reset(LowerLimit ?? -3, UpperLimit ?? 2, ticks);
            for (int i = 0; i < traces.Length; i++)
                foreach (var point in traces[i].Points) cache.Add(i, point);
        }
        Array.Copy(cache.Pixels, pixels, pixels.Length);
        int cursor = Math.Clamp((int)(PresentTime / HistorySeconds * (width - 1)), 0, width - 1);
        for (int y = 0; y < height; y++)
        {
            int index = y * width + cursor;
            if (pixels[index] == 0 || pixels[index] == 0x30303030) pixels[index] = unchecked((int)0x80808080);
        }
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(MemoryMarshal.AsBytes(pixels.AsSpan()));
        bitmap.Invalidate();
    }

    sealed class Trace(string metric, string name, Color color)
    {
        internal string Metric { get; } = metric;
        internal string Name { get; } = name;
        internal int Color { get; } = unchecked((int)(0xff000000u | (uint)color.R << 16 | (uint)color.G << 8 | color.B));
        internal List<ScatterPoint> Points { get; } = new(Capacity);
        internal List<TimingSample> Incoming { get; } = new();
        internal TimingHistory? History;
        internal long Cursor;
        internal bool Visible = true;
    }
}

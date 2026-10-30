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

/// <summary>Persistent scatter bitmap at roughly half physical resolution; no retained plot history.</summary>
internal sealed class TimingChart : UserControl
{
    int bitmapWidth, bitmapHeight;
    const int Capacity = 32768;
    const int HistorySeconds = FrameTimeView.HistorySeconds;
    readonly Trace[] traces;
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly Canvas yLabels = new();
    readonly Canvas secondBars = new() { IsHitTestVisible = false };
    readonly Canvas cursorLayer = new() { IsHitTestVisible = false };
    readonly Microsoft.UI.Xaml.Shapes.Rectangle cursor = new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(128, 255, 255, 255)),
    };
    readonly Grid plot = new();
    readonly string metric;
    WriteableBitmap? bitmap;
    int[] pixels = [];
    PipelineProfile? profile;
    long origin, lost;
    bool dirty = true;
    double latestTime;
    double[] ticks = [];
    internal int PointCount => traces[0].SampleCount;
    internal int PlottedPointCount => traces.Sum(t => t.SampleCount);
    internal int[] PointCounts => traces.Select(t => t.SampleCount).ToArray();
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
        cursorLayer.Children.Add(cursor);
        plot.Children.Add(cursorLayer);
        Grid.SetColumn(plot, 1);
        layout.Children.Add(plot);
        var legend = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
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
        plot.SizeChanged += (_, _) => UpdateLabels();
    }

    // Toggles gate future writes; existing marks remain.
    void SetVisible(int index, bool visible) => traces[index].Visible = visible;

    internal void Attach(PipelineProfile? next, long timestamp)
    {
        if (ReferenceEquals(profile, next)) return;
        profile = next;
        origin = timestamp;
        lost = 0;
        latestTime = 0;
        LowerLimit = UpperLimit = null;
        ticks = [];
        foreach (var trace in traces)
        {
            trace.History = next?.Track(trace.Metric, Capacity);
            trace.Cursor = 0;
            trace.SampleCount = 0;
            trace.Incoming.Clear();
        }
        dirty = true;
        UpdateLabels();
    }

    internal void Refresh(double now)
    {
        long start = Stopwatch.GetTimestamp();
        double rasterScale = XamlRoot?.RasterizationScale ?? 1;
        double physicalWidth = plot.ActualWidth * rasterScale, physicalHeight = plot.ActualHeight * rasterScale;
        if (physicalWidth <= 0 || physicalHeight <= 0) return;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var trace in traces)
        {
            if (trace.History != null) lost += trace.History.ReadSince(ref trace.Cursor, trace.Incoming);
            else trace.Incoming.Clear();
            if (!trace.Visible) { trace.Incoming.Clear(); continue; }
            foreach (var sample in trace.Incoming)
            {
                if (!double.IsFinite(sample.Milliseconds) || sample.Milliseconds <= 0) continue;
                double value = Math.Log10(sample.Milliseconds);
                min = Math.Min(min, value); max = Math.Max(max, value);
                trace.SampleCount++;
            }
        }
        double previousTime = latestTime;
        latestTime = now;
        UpdateCursor();
        // Expand only: contraction would unnecessarily discard accumulated marks.
        if (double.IsFinite(min) && double.IsFinite(max))
        {
            double lower = TimingScale.Floor(min), upper = TimingScale.Ceiling(max);
            if (upper <= lower) upper = TimingScale.Next(lower);
            lower = Math.Min(LowerLimit ?? lower, lower);
            upper = Math.Max(UpperLimit ?? upper, upper);
            if (LowerLimit != lower || UpperLimit != upper)
            {
                LowerLimit = lower; UpperLimit = upper;
                ticks = TimingScale.Ticks(lower, upper);
                dirty = true;
                UpdateLabels();
            }
        }
        bool resized = ResizeRaster(physicalWidth, physicalHeight);
        bool changed = dirty || resized;
        if (dirty)
        {
            Array.Clear(pixels);
        }
        else changed |= AdaptiveScatterRaster.ClearSweep(pixels, bitmapWidth, bitmapHeight, previousTime, now, HistorySeconds);
        profile?.Elapsed(metric.Replace("_update_ms", "_prepare_ms"), start);
        long rasterStart = Stopwatch.GetTimestamp();
        foreach (var trace in traces)
        {
            foreach (var sample in trace.Incoming)
            {
                if (!double.IsFinite(sample.Milliseconds) || sample.Milliseconds <= 0) continue;
                double time = Stopwatch.GetElapsedTime(origin, sample.Timestamp).TotalSeconds;
                int x = (int)(ScatterRaster.Wrap(time, HistorySeconds) / HistorySeconds * (bitmapWidth - 1));
                int y = Math.Clamp((int)((UpperLimit!.Value - Math.Log10(sample.Milliseconds)) /
                    (UpperLimit.Value - LowerLimit!.Value) * (bitmapHeight - 1)), 0, bitmapHeight - 1);
                int index = y * bitmapWidth + x;
                if (pixels[index] != trace.Color) { pixels[index] = trace.Color; changed = true; }
            }
            trace.Incoming.Clear();
        }
        if (!changed) return;
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
        UpdateCursor();
        yLabels.Children.Clear();
        secondBars.Children.Clear();
        double height = plot.ActualHeight, width = plot.ActualWidth;
        double scale = XamlRoot?.RasterizationScale ?? 1;
        if (LowerLimit is double lower && UpperLimit is double upper)
        {
            foreach (double tick in ticks)
            {
                var line = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = width, Height = 1 / scale,
                    Fill = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
                };
                double y = Math.Floor(Math.Max(0, Math.Ceiling(height * scale) - 1) * (upper - tick) / (upper - lower)) / scale;
                Canvas.SetTop(line, y);
                secondBars.Children.Add(line);
            }
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

    void UpdateCursor()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        double width = plot.ActualWidth;
        cursor.Width = 1 / scale;
        cursor.Height = plot.ActualHeight;
        double x = Math.Floor(Math.Max(0, Math.Ceiling(width * scale) - 1) * PresentTime / HistorySeconds) / scale;
        Canvas.SetLeft(cursor, x);
    }

    bool ResizeRaster(double physicalWidth, double physicalHeight)
    {
        int width = AdaptiveScatterRaster.Dimension(bitmapWidth, physicalWidth);
        int height = AdaptiveScatterRaster.Dimension(bitmapHeight, physicalHeight);
        if (width == bitmapWidth && height == bitmapHeight) return false;
        var nextPixels = new int[checked(width * height)];
        if (!dirty && pixels.Length != 0)
            AdaptiveScatterRaster.Restore(pixels, bitmapWidth, bitmapHeight, nextPixels, width, height);
        pixels = nextPixels;
        bitmapWidth = width; bitmapHeight = height;
        bitmap = null;
        UpdateLabels();
        return true;
    }

    void Render()
    {
        if (bitmap == null)
        {
            bitmap = new(bitmapWidth, bitmapHeight);
            image.Source = bitmap;
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
        // Accepted samples since Attach; no plotted points are retained.
        internal int SampleCount;
        internal List<TimingSample> Incoming { get; } = new();
        internal TimingHistory? History;
        internal long Cursor;
        internal bool Visible = true;
    }
}

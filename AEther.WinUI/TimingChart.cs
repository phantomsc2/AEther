using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace AEther.WinUI;

/// <summary>Incremental scatter bitmap with retained history for exact redraws after resize.</summary>
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
    double latestTime, lastSummaryTime;
    readonly double[] ticks = [-2, -1, 0, 1, 2];
    double? frameBudget;
    internal int PointCount => traces[0].SampleCount;
    internal int PlottedPointCount => traces.Sum(t => t.SampleCount);
    internal int[] PointCounts => traces.Select(t => t.SampleCount).ToArray();
    internal string[] CurveNames => traces.Select(t => t.Name).ToArray();
    internal double? LowerLimit => -2;
    internal double? UpperLimit => 2;
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
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(2, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });

        layout.Children.Add(yLabels);
        var unit = new TextBlock
        {
            Text = "ms", FontSize = 11, Opacity = .7,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(unit, 1);
        layout.Children.Add(unit);
        plot.Children.Add(secondBars);
        plot.Children.Add(image);
        cursorLayer.Children.Add(cursor);
        plot.Children.Add(cursorLayer);
        Grid.SetColumn(plot, 2);
        layout.Children.Add(plot);
        var legend = new Grid
        {
            RowSpacing = 4, ColumnSpacing = 6,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0)
        };
        legend.ColumnDefinitions.Add(new() { Width = new GridLength(88) });
        for (int column = 1; column <= 3; column++)
            legend.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        legend.RowDefinitions.Add(new() { Height = GridLength.Auto });
        string[] headings = ["Mean", "Std", "Max"];
        for (int column = 1; column <= 3; column++)
            AddCell(new TextBlock
            {
                Text = headings[column - 1], FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Left
            }, 0, column);
        ToolTipService.SetToolTip(legend,
            "Milliseconds received since the previous summary update (about 1 second). Std is population standard deviation.");
        for (int i = 0; i < definitions.Length; i++)
        {
            int index = definitions.Length - 1 - i;
            var d = definitions[index];
            legend.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var label = new TextBlock { Text = d.Name, FontSize = 11 };
            var token = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
            {
                Content = label, IsChecked = true, Width = 88, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(14),
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var fill = new SolidColorBrush(d.Color);
            foreach (string state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
            {
                token.Resources["ToggleButtonBackground" + state] = fill;
                token.Resources["ToggleButtonBorderBrush" + state] = fill;
                token.Resources["ToggleButtonForeground" + state] = new SolidColorBrush(Microsoft.UI.Colors.Black);
            }

            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(token, d.Name);
            ToolTipService.SetToolTip(token, d.Description);
            token.Checked += (_, _) => SetVisible(index, true);
            token.Unchecked += (_, _) => SetVisible(index, false);
            AddCell(token, i + 1, 0);
            AddCell(traces[index].MeanText, i + 1, 1);
            AddCell(traces[index].StdText, i + 1, 2);
            AddCell(traces[index].MaxText, i + 1, 3);
        }

        Grid.SetColumn(legend, 3);
        layout.Children.Add(legend);
        Content = layout;
        plot.SizeChanged += (_, _) => UpdateLabels();
        SizeChanged += (_, _) => UpdateLegendLayout();
        UpdateLegendLayout();

        void UpdateLegendLayout()
        {
            // Keep enough room for three complete numeric columns. On narrow
            // windows the legend becomes a table underneath the full-width plot.
            bool narrow = ActualWidth < 760;
            layout.ColumnDefinitions[3].Width = narrow ? new GridLength(0) : new GridLength(280);
            Grid.SetColumn(legend, narrow ? 0 : 3);
            Grid.SetColumnSpan(legend, narrow ? 4 : 1);
            Grid.SetRow(legend, narrow ? 1 : 0);
            legend.Margin = narrow ? new Thickness(8, 12, 8, 0) : new Thickness(8, 0, 8, 0);
        }

        void AddCell(FrameworkElement cell, int row, int column)
        {
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            cell.VerticalAlignment = VerticalAlignment.Center;
            legend.Children.Add(cell);
        }
    }

    // Toggles gate future writes; existing marks remain.
    void SetVisible(int index, bool visible) => traces[index].Visible = visible;

    internal void SetFrameBudget(double? milliseconds)
    {
        double? next = milliseconds is double value && double.IsFinite(value) && value > 0 ? value : null;
        if (frameBudget == next) return;
        frameBudget = next;
        UpdateLabels();
    }

    internal void Attach(PipelineProfile? next, long timestamp)
    {
        if (ReferenceEquals(profile, next)) return;
        profile = next;
        origin = timestamp;
        lost = 0;
        latestTime = 0;
        lastSummaryTime = 0;
        foreach (var trace in traces)
        {
            trace.History = next?.Track(trace.Metric, Capacity);
            trace.Cursor = 0;
            trace.SampleCount = 0;
            trace.Incoming.Clear();
            trace.NewPoints.Clear();
            trace.Points.Clear();
            trace.Summary.Reset();
            trace.ShowSummary();
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
        foreach (var trace in traces)
        {
            trace.NewPoints.Clear();
            if (trace.History != null) lost += trace.History.ReadSince(ref trace.Cursor, trace.Incoming);
            else trace.Incoming.Clear();
            foreach (var sample in trace.Incoming)
            {
                trace.Summary.Add(sample.Milliseconds);
                if (!trace.Visible) continue;
                if (!double.IsFinite(sample.Milliseconds) || sample.Milliseconds <= 0) continue;
                trace.SampleCount++;
                var point = new ScatterPoint(
                    Stopwatch.GetElapsedTime(origin, sample.Timestamp).TotalSeconds,
                    Math.Log10(sample.Milliseconds));
                if (point.Time < now - HistorySeconds || point.LogMilliseconds < LowerLimit!.Value
                    || point.LogMilliseconds > UpperLimit!.Value) continue;
                trace.Points.Enqueue(point);
                trace.NewPoints.Add(point);
            }

            while (trace.Points.TryPeek(out var point) && point.Time < now - HistorySeconds)
                trace.Points.Dequeue();
            trace.Incoming.Clear();
        }

        if (now - lastSummaryTime >= 1)
        {
            foreach (var trace in traces)
            {
                trace.ShowSummary();
                trace.Summary.Reset();
            }

            lastSummaryTime = now;
        }

        double previousTime = latestTime;
        latestTime = now;
        UpdateCursor();
        bool resized = ResizeRaster(physicalWidth, physicalHeight);
        bool changed = dirty || resized;
        if (dirty || resized)
            Array.Clear(pixels);
        else
            changed |= AdaptiveScatterRaster.ClearSweep(pixels, bitmapWidth, bitmapHeight, previousTime, now,
                HistorySeconds);

        profile?.Elapsed(metric.Replace("_update_ms", "_prepare_ms"), start);
        long rasterStart = Stopwatch.GetTimestamp();
        if (dirty || resized)
        {
            foreach (var trace in traces)
                changed |= Draw(trace.Points, trace.Color);
        }
        else
        {
            foreach (var trace in traces)
                changed |= Draw(trace.NewPoints, trace.Color);
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

    bool Draw(IEnumerable<ScatterPoint> points, int color)
    {
        bool changed = false;
        double xScale = (bitmapWidth - 1.0) / HistorySeconds;
        double yScale = (bitmapHeight - 1.0) / (UpperLimit!.Value - LowerLimit!.Value);
        foreach (var point in points)
        {
            int x = (int)(ScatterRaster.Wrap(point.Time, HistorySeconds) * xScale);
            int y = (int)((UpperLimit.Value - point.LogMilliseconds) * yScale);
            int index = y * bitmapWidth + x;
            if (pixels[index] == color) continue;
            pixels[index] = color;
            changed = true;
        }
        return changed;
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
                double y =
                    Math.Floor(Math.Max(0, Math.Ceiling(height * scale) - 1) * (upper - tick) / (upper - lower)) /
                    scale;
                Canvas.SetTop(line, y);
                secondBars.Children.Add(line);
            }

            int step = Math.Max(1, (int)Math.Ceiling(ticks.Length / Math.Max(2, height / 18)));
            for (int i = 0; i < ticks.Length; i += step)
            {
                var label = new TextBlock
                {
                    Text = Math.Pow(10, ticks[i]).ToString("G3", CultureInfo.CurrentCulture), FontSize = 11, Width = 60,
                    TextAlignment = TextAlignment.Center
                };
                Canvas.SetTop(label,
                    Math.Clamp((upper - ticks[i]) / (upper - lower) * height - 7, 0, Math.Max(0, height - 14)));
                Canvas.SetLeft(label, 0);
                yLabels.Children.Add(label);
            }
        }

        for (int i = 0; i <= HistorySeconds * 2; i++)
        {
            var bar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 1 / scale, Height = height,
                Fill = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)),
            };
            Canvas.SetLeft(bar,
                Math.Floor(Math.Max(0, Math.Ceiling(width * scale) - 1) * i / (HistorySeconds * 2)) / scale);
            secondBars.Children.Add(bar);
        }

        if (frameBudget is double budget && LowerLimit is double min && UpperLimit is double max)
        {
            double value = Math.Log10(budget);
            if (value < min || value > max) return;
            var line = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = width, Height = 5 / scale,
                Fill = new SolidColorBrush(Color.FromArgb(80, 255, 80, 80)),
            };
            double y = Math.Floor(Math.Max(0, Math.Ceiling(height * scale) - 1) * (max - value) / (max - min));
            Canvas.SetTop(line, (y - 2) / scale);
            secondBars.Children.Add(line);
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
        int width = AdaptiveScatterRaster.Dimension(physicalWidth);
        int height = AdaptiveScatterRaster.Dimension(physicalHeight);
        if (width == bitmapWidth && height == bitmapHeight) return false;
        pixels = new int[checked(width * height)];
        bitmapWidth = width;
        bitmapHeight = height;
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

        internal int Color { get; } =
            unchecked((int)(0xff000000u | (uint)color.R << 16 | (uint)color.G << 8 | color.B));

        // Accepted samples since Attach, plus the bounded points needed for resize redraws.
        internal int SampleCount;
        internal List<TimingSample> Incoming { get; } = new();
        internal List<ScatterPoint> NewPoints { get; } = new();
        internal Queue<ScatterPoint> Points { get; } = new();
        internal TimingHistory? History;
        internal long Cursor;
        internal bool Visible = true;
        internal TimingSummary Summary;
        internal TextBlock MeanText { get; } = SummaryText();
        internal TextBlock StdText { get; } = SummaryText();
        internal TextBlock MaxText { get; } = SummaryText();

        internal void ShowSummary()
        {
            MeanText.Text = Format(Summary.Mean);
            StdText.Text = Format(Summary.StandardDeviation);
            MaxText.Text = Format(Summary.Max);
        }

        string Format(double value) => Summary.Count == 0 ? "—" : value.ToString("G3", CultureInfo.CurrentCulture);

        static TextBlock SummaryText() => new()
        {
            Text = "—", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left,
        };
    }
}

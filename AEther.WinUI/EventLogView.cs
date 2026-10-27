using System.Collections.ObjectModel;
using System.Globalization;
using AEther.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Serilog.Events;

namespace AEther.WinUI;

public sealed class EventLogView : UserControl
{
    readonly ObservableCollection<EventRow> rows = new();
    readonly EventLogStore store;
    readonly Grid panel = new() { Visibility = Visibility.Collapsed };
    readonly Button indicator = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(10, 4, 10, 4), MinHeight = 28 };
    readonly FontIcon icon = new() { FontSize = 14 };
    readonly TextBlock label = new();
    readonly ListView list = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    ScrollViewer? scroller;
    long version = -1;
    bool follow = true;
    internal IndicatorState State { get; private set; }
    internal bool IsExpanded => panel.Visibility == Visibility.Visible;
    internal ListView Events => list;

    public EventLogView(EventLogStore? store = null)
    {
        this.store = store ?? ApplicationLog.Events;
        var layout = new Grid { RowSpacing = 4 };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(list);
        layout.Children.Add(panel);
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        status.Children.Add(icon); status.Children.Add(label);
        indicator.Content = status;
        Grid.SetRow(indicator, 1);
        layout.Children.Add(indicator);
        Content = layout;
        AutomationProperties.SetName(list, "Application event log");
        // Keep the standard ListViewItem style, with a smaller minimum for single-line events.
        list.Resources["ListViewItemMinHeight"] = 28d;
        list.ItemsSource = rows;
        list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding Summary}" TextWrapping="Wrap" TextTrimming="CharacterEllipsis" MaxLines="2" FontSize="12" />
            </DataTemplate>
            """);
        list.Loaded += (_, _) =>
        {
            scroller = FindScroller(list);
            if (scroller != null) scroller.ViewChanged += (_, _) =>
                follow = scroller.ScrollableHeight - scroller.VerticalOffset < 4;
        };
        indicator.Click += (_, _) => Toggle();
        ActualThemeChanged += (_, _) => UpdateIndicator();
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
        UpdateIndicator();
    }

    internal void SetAvailableHeight(double height) => panel.Height = Math.Max(0, Math.Min(240, height * .4));

    internal void Toggle()
    {
        panel.Visibility = IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        UpdateIndicator();
        if (IsExpanded && follow && rows.Count != 0)
            DispatcherQueue.TryEnqueue(() => list.ScrollIntoView(rows[^1]));
    }

    internal void Refresh()
    {
        var snapshot = store.ChangesSince(version);
        if (snapshot == null) return;
        version = snapshot.Version;
        State = snapshot.State;
        UpdateIndicator();
        var first = snapshot.Events.FirstOrDefault()?.Sequence ?? long.MaxValue;
        while (rows.Count != 0 && rows[0].Sequence < first) rows.RemoveAt(0);
        var last = rows.LastOrDefault()?.Sequence ?? 0;
        foreach (var entry in snapshot.Events)
            if (entry.Sequence > last) rows.Add(new(entry));
        if (IsExpanded && follow && rows.Count != 0)
            DispatcherQueue.TryEnqueue(() => { if (IsLoaded && follow && rows.Count != 0) list.ScrollIntoView(rows[^1]); });
    }

    void UpdateIndicator()
    {
        label.Text = State switch { IndicatorState.Error => "Pipeline error", IndicatorState.Warning => "Warnings occurred", _ => "Events" };
        icon.Glyph = State switch { IndicatorState.Error => "\uEA39", IndicatorState.Warning => "\uE7BA", _ => "\uE946" };
        string resource = State switch
        {
            IndicatorState.Error => "SystemFillColorCriticalBrush",
            IndicatorState.Warning => "SystemFillColorCautionBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        icon.Foreground = (Brush)Application.Current.Resources[resource];
        AutomationProperties.SetName(indicator, $"{label.Text}. {(IsExpanded ? "Collapse" : "Expand")} application event log");
        ToolTipService.SetToolTip(indicator, "Click to expand or collapse application events. Problems remain indicated until the audio pipeline successfully restarts.");
    }

    static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    public sealed class EventRow
    {
        public long Sequence { get; }
        public string Summary { get; }
        public EventRow(AppEvent entry)
        {
            Sequence = entry.Sequence;
            var evt = entry.Event;
            var source = evt.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue scalar
                ? scalar.Value?.ToString() : "Application";
            var message = evt.RenderMessage(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(evt.Exception?.Message)) message += $": {evt.Exception.Message}";
            Summary = $"{evt.Timestamp.ToLocalTime():HH:mm:ss.fff}  {evt.Level}  {source}  {message}";
        }
        public override string ToString() => Summary;
    }
}

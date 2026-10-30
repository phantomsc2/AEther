using System.Collections.ObjectModel;
using System.Globalization;
using AEther.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Tabular;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Serilog.Events;

namespace AEther.WinUI;

public sealed class EventLogView : UserControl
{
    readonly ObservableCollection<EventRow> rows = new();
    readonly EventLogStore store;
    readonly Grid panel = new() { Visibility = Visibility.Collapsed };
    readonly Button indicator = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        Padding = new Thickness(10, 4, 10, 4), MinHeight = 28 };
    readonly StackPanel summary = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    readonly TableView table = (TableView)XamlReader.Load("""
        <tabular:TableView xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                          xmlns:tabular="using:Microsoft.UI.Xaml.Controls.Tabular"
                          Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                          RowBackground="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                          Density="Compact" CanUserSortColumns="False" IsReadOnly="True">
            <tabular:TableView.Resources>
                <Style x:Key="DebugIconStyle" TargetType="FontIcon">
                    <Setter Property="Glyph" Value="&#xEBE8;" />
                    <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
                </Style>
                <Style x:Key="InformationIconStyle" TargetType="FontIcon">
                    <Setter Property="Glyph" Value="&#xE946;" />
                    <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
                </Style>
                <Style x:Key="WarningIconStyle" TargetType="FontIcon">
                    <Setter Property="Glyph" Value="&#xE7BA;" />
                    <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCautionBrush}" />
                </Style>
                <Style x:Key="ErrorIconStyle" TargetType="FontIcon">
                    <Setter Property="Glyph" Value="&#xEA39;" />
                    <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}" />
                </Style>
                <Style x:Key="FatalIconStyle" TargetType="FontIcon">
                    <Setter Property="Glyph" Value="&#xE711;" />
                    <Setter Property="Foreground" Value="{ThemeResource SystemFillColorCriticalBrush}" />
                </Style>
            </tabular:TableView.Resources>
            <tabular:TableView.Columns>
                <tabular:TableViewTextColumn Header="Time" Binding="{Binding Time}" Width="Auto" />
                <tabular:TableViewTemplateColumn Header="Severity" HeaderToolTip="Severity" Width="Auto"
                                                CellToolTipBinding="{Binding Severity}">
                    <tabular:TableViewTemplateColumn.HeaderTemplate>
                        <DataTemplate>
                            <FontIcon Glyph="&#xE946;" FontSize="14" AutomationProperties.AccessibilityView="Raw" />
                        </DataTemplate>
                    </tabular:TableViewTemplateColumn.HeaderTemplate>
                    <tabular:TableViewTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <FontIcon Style="{Binding SeverityIconStyle}" FontSize="14"
                                      AutomationProperties.Name="{Binding Severity}" />
                        </DataTemplate>
                    </tabular:TableViewTemplateColumn.CellTemplate>
                </tabular:TableViewTemplateColumn>
                <tabular:TableViewTextColumn Header="Source" Binding="{Binding Source}" Width="Auto" MaxWidth="240"
                                            CellToolTipBinding="{Binding Source}" />
                <tabular:TableViewTextColumn Header="Message" Binding="{Binding Message}" Width="*"
                                            CellToolTipBinding="{Binding Message}" />
            </tabular:TableView.Columns>
        </tabular:TableView>
        """);
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    ScrollViewer? scroller;
    double lastScrollOffset;
    long version = -1;
    bool follow = true;
    internal bool IsExpanded => panel.Visibility == Visibility.Visible;
    internal TableView Events => table;
    internal StackPanel StatusSummary => summary;

    public EventLogView(EventLogStore? store = null)
    {
        this.store = store ?? ApplicationLog.Events;
        var layout = (Grid)XamlReader.Load("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  RowSpacing="4" />
            """);
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.Children.Add(table);
        layout.Children.Add(panel);
        indicator.Content = summary;
        Grid.SetRow(indicator, 1);
        layout.Children.Add(indicator);
        Content = layout;
        AutomationProperties.SetName(table, "Application event log");
        table.ItemsSource = rows;
        // The native table builds its scrollers after Loaded, during its first layout.
        table.LayoutUpdated += AttachScroller;
        indicator.Click += (_, _) => Toggle();
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
        UpdateAccessibility();
    }

    internal void SetAvailableHeight(double height) => panel.Height = Math.Max(0, Math.Min(240, height * .4));

    internal void Toggle()
    {
        panel.Visibility = IsExpanded ? Visibility.Collapsed : Visibility.Visible;
        UpdateAccessibility();
        if (IsExpanded && follow && rows.Count != 0)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScrollToEnd);
    }

    internal void Refresh()
    {
        var snapshot = store.ChangesSince(version);
        if (snapshot == null) return;
        version = snapshot.Version;
        UpdateSummary(snapshot.Events);
        var first = snapshot.Events.FirstOrDefault()?.Sequence ?? long.MaxValue;
        while (rows.Count != 0 && rows[0].Sequence < first) rows.RemoveAt(0);
        var last = rows.LastOrDefault()?.Sequence ?? 0;
        foreach (var entry in snapshot.Events)
            if (entry.Sequence > last) rows.Add(new(entry, table.Resources));
        if (IsExpanded && follow && rows.Count != 0)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScrollToEnd);
    }

    void ScrollToEnd()
    {
        if (IsLoaded && follow && rows.Count != 0)
            scroller?.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
    }

    void AttachScroller(object? sender, object e)
    {
        scroller = FindScroller(table);
        if (scroller == null) return;
        table.LayoutUpdated -= AttachScroller;
        scroller.ViewChanged += (_, _) =>
        {
            // Growing the extent alone must not stop following new events.
            if (scroller.VerticalOffset == lastScrollOffset) return;
            lastScrollOffset = scroller.VerticalOffset;
            follow = scroller.ScrollableHeight - scroller.VerticalOffset < 4;
        };
        scroller.SizeChanged += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScrollToEnd);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ScrollToEnd);
    }

    void UpdateSummary(IEnumerable<AppEvent> events)
    {
        summary.Children.Clear();
        foreach (var group in events.GroupBy(entry => entry.Event.Level).OrderByDescending(group => group.Key))
        {
            var count = group.Count();
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            pair.Children.Add(new FontIcon { Style = (Style)table.Resources[SeverityStyle(group.Key)], FontSize = 14 });
            pair.Children.Add(new TextBlock { Text = count.ToString(CultureInfo.CurrentCulture), VerticalAlignment = VerticalAlignment.Center });
            AutomationProperties.SetName(pair, $"{group.Key}: {count}");
            ToolTipService.SetToolTip(pair, $"{group.Key}: {count}");
            summary.Children.Add(pair);
        }
        UpdateAccessibility();
    }

    void UpdateAccessibility()
    {
        var counts = string.Join(", ", summary.Children.Select(AutomationProperties.GetName));
        var action = $"{(IsExpanded ? "Collapse" : "Expand")} application event log";
        AutomationProperties.SetName(indicator, counts.Length == 0 ? action : $"{counts}. {action}");
        ToolTipService.SetToolTip(indicator, action);
    }

    static string SeverityStyle(LogEventLevel severity) => severity switch
    {
        LogEventLevel.Fatal => "FatalIconStyle",
        LogEventLevel.Error => "ErrorIconStyle",
        LogEventLevel.Warning => "WarningIconStyle",
        LogEventLevel.Debug => "DebugIconStyle",
        _ => "InformationIconStyle",
    };

    static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            // TableView has separate header and body scrollers; only the body follows events.
            if (child is ScrollViewer { Name: "PART_BodyScroller" } scroll) return scroll;
            if (FindScroller(child) is { } nested) return nested;
        }
        return null;
    }

    public sealed class EventRow
    {
        public long Sequence { get; }
        public string Time { get; }
        public string Severity { get; }
        public Style SeverityIconStyle { get; }
        public string Source { get; }
        public string Message { get; }
        public EventRow(AppEvent entry, ResourceDictionary resources)
        {
            Sequence = entry.Sequence;
            var evt = entry.Event;
            Time = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            Severity = evt.Level.ToString();
            SeverityIconStyle = (Style)resources[SeverityStyle(evt.Level)];
            Source = evt.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue scalar
                ? scalar.Value?.ToString() ?? "Application" : "Application";
            Message = evt.RenderMessage(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(evt.Exception?.Message)) Message += $": {evt.Exception.Message}";
        }
        public override string ToString() => $"{Time} {Severity} {Source} {Message}";
    }
}

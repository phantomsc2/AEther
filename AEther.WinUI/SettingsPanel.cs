using AEther.NAudio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;
using Serilog;

namespace AEther.WinUI;

public sealed class SettingsPanel : UserControl
{
    public const string Loopback = "System audio (loopback)";
    readonly ComboBox input = new() { Header = "Input", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox scenePicker = new() { Name = "Scene", Header = "Scene", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ShaderPanel shaders;
    bool refreshingScenes;
    internal Grid Columns { get; } = new() { Padding = new Thickness(12), ColumnSpacing = 4, RowSpacing = 8 };
    internal Grid AudioColumn { get; } = new() { RowSpacing = 8 };
    internal Grid SceneColumn { get; } = new() { RowSpacing = 8 };
    internal ScrollViewer AudioSettings { get; }
    internal EventLogView EventLog { get; } = new();
    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public event EventHandler? InputChanged;
    public event Action<string>? SceneSelected;

    public SettingsPanel(AudioPanel audio, GraphicsPanel graphics, ShaderPanel shaders, AnalysisView analysis, FrameTimeView frameTime)
    {
        this.shaders = shaders;
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(Columns);
        Grid.SetRow(EventLog, 1);
        layout.Children.Add(EventLog);
        Content = layout;
        SizeChanged += (_, _) => EventLog.SetAvailableHeight(ActualHeight);
        Columns.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        const double defaultAnalysisHeight = 240;
        Columns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(defaultAnalysisHeight), MinHeight = 32 }); // 0: live views
        Columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: divider
        var controlsRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        Columns.RowDefinitions.Add(controlsRow); // 2: controls

        var divider = new GridSplitter
        {
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridSplitter.GridResizeDirection.Rows,
            ResizeBehavior = GridSplitter.GridResizeBehavior.PreviousAndNext,
        };
        AutomationProperties.SetName(divider, "Resize live view and controls");
        ToolTipService.SetToolTip(divider, "Drag to resize. Arrow keys adjust; double-click restores the default height.");
        divider.DoubleTapped += (_, e) =>
        {
            Columns.RowDefinitions[0].Height = new GridLength(defaultAnalysisHeight);
            controlsRow.Height = new GridLength(1, GridUnitType.Star);
            e.Handled = true;
        };
        Grid.SetRow(divider, 1);
        Grid.SetColumnSpan(divider, 3);
        Columns.Children.Add(divider);

        var columnDivider = new GridSplitter
        {
            Width = 8,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridSplitter.GridResizeDirection.Columns,
            ResizeBehavior = GridSplitter.GridResizeBehavior.PreviousAndNext,
        };
        AutomationProperties.SetName(columnDivider, "Resize audio and video columns");
        Grid.SetColumn(columnDivider, 1);
        Grid.SetRowSpan(columnDivider, 3);
        Columns.Children.Add(columnDivider);

        // Row 0: Audio analysis (left), frame-time timeline (right).
        analysis.MinHeight = 32;
        Grid.SetRow(analysis, 0);
        Columns.Children.Add(analysis);
        Grid.SetColumn(frameTime, 2);
        Columns.Children.Add(frameTime);

        // Row 2: Audio controls (left column)
        AudioColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: input
        AudioColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: AudioSettings
        var audioStack = (StackPanel)audio.Content;
        audioStack.Children.Insert(0, audio.GainEditor);
        AudioSettings = new ScrollViewer
        {
            Content = audio,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(input, 0); Grid.SetRow(AudioSettings, 1);
        AudioColumn.Children.Add(input);
        AudioColumn.Children.Add(AudioSettings);
        Grid.SetRow(AudioColumn, 2);
        Columns.Children.Add(AudioColumn);

        // Row 2: Video controls (right column)
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: scenePicker
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: graphics
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2: shaders
        AutomationProperties.SetName(scenePicker, "Scene");
        Grid.SetRow(graphics, 1);
        Grid.SetRow(scenePicker, 0);
        Grid.SetRow(shaders, 2);
        SceneColumn.Children.Add(scenePicker);
        SceneColumn.Children.Add(graphics);
        SceneColumn.Children.Add(shaders);
        Grid.SetRow(SceneColumn, 2);
        Grid.SetColumn(SceneColumn, 2);
        Columns.Children.Add(SceneColumn);
        scenePicker.SelectionChanged += (_, _) =>
        {
            if (!refreshingScenes && scenePicker.SelectedItem is string name) SceneSelected?.Invoke(name);
        };
        input.Items.Add(Loopback);
        try { foreach (var name in Recorder.GetAvailableDeviceNames()) input.Items.Add(name); }
        catch (Exception error) { Log.ForContext<SettingsPanel>().Warning(error, "Could not enumerate audio input devices"); }
        input.SelectedIndex = 0;
        input.SelectionChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetScenes(IEnumerable<string> names, string? selected)
    {
        refreshingScenes = true;
        try
        {
            scenePicker.Items.Clear();
            foreach (var name in names.Order(StringComparer.Ordinal)) scenePicker.Items.Add(name);
            scenePicker.SelectedItem = selected;
            scenePicker.IsEnabled = scenePicker.Items.Count != 0;
            shaders.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { refreshingScenes = false; }
    }
}

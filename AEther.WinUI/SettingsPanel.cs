using AEther.NAudio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;

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
    internal Expander Diagnostics { get; }
    public bool DiagnosticsExpanded => Diagnostics.IsExpanded;
    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public string? DeviceError { get; private set; }
    public event EventHandler? InputChanged;
    public event Action<string>? SceneSelected;
    public event EventHandler? DiagnosticsRequested;

    public SettingsPanel(AudioPanel audio, ShaderPanel shaders, DebugPanel debug, AnalysisView analysis, FrameTimeView frameTime)
    {
        this.shaders = shaders;
        Content = Columns;
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
        Diagnostics = new Expander
        {
            Header = "Debug", Content = debug, IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        Diagnostics.Expanding += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        var audioStack = (StackPanel)audio.Content;
        audioStack.Children.Insert(0, audio.GainEditor);
        audioStack.Children.Add(Diagnostics);
        AudioSettings = new ScrollViewer
        {
            Content = audio,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Diagnostics.Expanding += (_, _) => CrashLog.Write("Debug panel expanding");
        Diagnostics.Collapsed += (_, _) => CrashLog.Write("Debug panel collapsed");
        AudioSettings.AddHandler(UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
            {
                if (!DiagnosticsExpanded) return;
                var point = e.GetCurrentPoint(AudioSettings);
                CrashLog.Write($"Debug scroll wheel: delta={point.Properties.MouseWheelDelta}; "
                    + $"offset={AudioSettings.VerticalOffset:F1}; extent={AudioSettings.ExtentHeight:F1}; viewport={AudioSettings.ViewportHeight:F1}");
            }), handledEventsToo: true);
        AudioSettings.ViewChanging += (_, e) =>
        {
            if (DiagnosticsExpanded)
                CrashLog.Write($"Debug scroll changing: current={AudioSettings.VerticalOffset:F1}; next={e.NextView.VerticalOffset:F1}; final={e.FinalView.VerticalOffset:F1}");
        };
        AudioSettings.ViewChanged += (_, e) =>
        {
            if (DiagnosticsExpanded && !e.IsIntermediate)
                CrashLog.Write($"Debug scroll finished: offset={AudioSettings.VerticalOffset:F1}");
        };
        Grid.SetRow(input, 0); Grid.SetRow(AudioSettings, 1);
        AudioColumn.Children.Add(input);
        AudioColumn.Children.Add(AudioSettings);
        Grid.SetRow(AudioColumn, 2);
        Columns.Children.Add(AudioColumn);

        // Row 2: Video controls (right column)
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: scenePicker
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: shaders
        AutomationProperties.SetName(scenePicker, "Scene");
        Grid.SetRow(scenePicker, 0);
        Grid.SetRow(shaders, 1);
        SceneColumn.Children.Add(scenePicker);
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
        catch (Exception error) { DeviceError = error.Message; }
        input.SelectedIndex = 0;
        input.SelectionChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowDiagnostics()
    {
        Diagnostics.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() => Diagnostics.StartBringIntoView());
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

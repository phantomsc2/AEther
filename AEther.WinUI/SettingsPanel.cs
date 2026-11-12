using AEther.NAudio;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace AEther.WinUI;

public sealed class SettingsPanel : UserControl
{
    public const string Loopback = "System audio (loopback)";
    readonly ComboBox input = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    readonly ComboBox scenePicker = new()
        { Name = "Scene", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };

    readonly ShaderPanel shaders;
    bool refreshingScenes;

    internal TabView Tabs { get; } = new()
    {
        IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false,
        TabWidthMode = TabViewWidthMode.Equal,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    // Named surfaces are retained for diagnostics and profile tooling.
    internal Grid Columns { get; } = new() { Padding = new Thickness(12), RowSpacing = 0 };
    internal Grid AudioColumn { get; } = new() { RowSpacing = 8 };
    internal Grid SceneColumn { get; } = new() { Padding = new Thickness(12), RowSpacing = 0 };
    internal ScrollViewer AudioSettings { get; private set; } = null!;
    internal EventLogView EventLog { get; } = new();

    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public event EventHandler? InputChanged;
    public event Action<string>? SceneSelected;

    public SettingsPanel(AudioPanel audio, GraphicsPanel graphics, ShaderPanel shaders, AnalysisView analysis,
        FrameTimeView backlog, double previewAspectRatio)
    {
        _ = previewAspectRatio;
        this.shaders = shaders;
        var chrome = (Brush)Application.Current.Resources["WindowChromeBrush"];
        var contentSurface = (Brush)Application.Current.Resources["SolidBackgroundFillColorTertiaryBrush"];
        Tabs.Background = chrome;
        Columns.Background = contentSurface;
        SceneColumn.Background = contentSurface;
        var root = new Grid { Background = chrome };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(Tabs);
        Grid.SetRow(EventLog, 1);
        root.Children.Add(EventLog);
        Content = root;
        SizeChanged += (_, _) => EventLog.SetAvailableHeight(ActualHeight);

        BuildAudioTab(audio, analysis);
        BuildVideoTab(graphics, backlog);
        Tabs.TabItems.Add(CreateTab("Audio", Symbol.Volume, Columns));
        Tabs.TabItems.Add(CreateTab("Video", Symbol.Video, SceneColumn));
        Tabs.SelectedIndex = 0;

        scenePicker.SelectionChanged += (_, _) =>
        {
            if (!refreshingScenes && scenePicker.SelectedItem is string name) SceneSelected?.Invoke(name);
        };
        input.Items.Add(Loopback);
        try
        {
            foreach (var name in Recorder.GetAvailableDeviceNames()) input.Items.Add(name);
        }
        catch (Exception error)
        {
            Log.ForContext<SettingsPanel>().Warning(error, "Could not enumerate audio input devices");
        }

        input.SelectedIndex = 0;
        input.SelectionChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
    }

    static TabViewItem CreateTab(string header, Symbol symbol, UIElement content) => new()
    {
        Header = header,
        IconSource = new SymbolIconSource { Symbol = symbol },
        IsClosable = false,
        Content = content,
    };

    static GridSplitter CreateHorizontalSplitter() => new()
    {
        Height = 8,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        ResizeDirection = GridSplitter.GridResizeDirection.Rows,
        ResizeBehavior = GridSplitter.GridResizeBehavior.PreviousAndNext
    };

    void BuildAudioTab(AudioPanel audio, AnalysisView analysis)
    {
        Columns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 96 });
        Columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Columns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 96 });
        analysis.MinHeight = 96;
        Columns.Children.Add(analysis);
        var splitter = CreateHorizontalSplitter();
        Grid.SetRow(splitter, 1);
        Columns.Children.Add(splitter);

        AudioColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AudioColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AudioSettings = new ScrollViewer
        {
            Content = audio,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        AudioColumn.Children.Add(LabeledControl.Create("Input", input));
        Grid.SetRow(AudioSettings, 1);
        AudioColumn.Children.Add(AudioSettings);
        Grid.SetRow(AudioColumn, 2);
        Columns.Children.Add(AudioColumn);
    }

    void BuildVideoTab(GraphicsPanel graphics, FrameTimeView backlog)
    {
        SceneColumn.RowDefinitions.Add(new RowDefinition
            { Height = new GridLength(1, GridUnitType.Star), MinHeight = 96 });
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SceneColumn.RowDefinitions.Add(new RowDefinition
            { Height = new GridLength(1, GridUnitType.Star), MinHeight = 96 });
        backlog.MinHeight = 96;
        SceneColumn.Children.Add(backlog);
        var splitter = CreateHorizontalSplitter();
        Grid.SetRow(splitter, 1);
        SceneColumn.Children.Add(splitter);

        AutomationProperties.SetName(scenePicker, "Scene");
        var sceneRow = LabeledControl.Create("Scene", scenePicker);
        Grid.SetRow(sceneRow, 2);
        Grid.SetRow(graphics, 3);
        Grid.SetRow(shaders, 4);
        SceneColumn.Children.Add(sceneRow);
        SceneColumn.Children.Add(graphics);
        SceneColumn.Children.Add(shaders);
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
        finally
        {
            refreshingScenes = false;
        }
    }
}
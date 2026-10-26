using AEther.NAudio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

public sealed class SettingsPanel : UserControl
{
    public const string Loopback = "System audio (loopback)";
    readonly ComboBox input = new() { Header = "Input", HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox scenePicker = new() { Name = "Scene", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ShaderPanel shaders;
    bool refreshingScenes;
    internal Grid Columns { get; } = new() { Padding = new Thickness(16), ColumnSpacing = 16, RowSpacing = 12 };
    internal Grid AudioColumn { get; } = new() { RowSpacing = 12 };
    internal Grid SceneColumn { get; } = new() { RowSpacing = 12 };
    internal Expander AudioSettings { get; }
    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public string? DeviceError { get; private set; }
    public event EventHandler? InputChanged;
    public event Action<string>? SceneSelected;

    public SettingsPanel(AudioPanel audio, ShaderPanel shaders, ToolWindow debug, AnalysisView analysis)
    {
        this.shaders = shaders;
        Content = Columns;
        Columns.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Columns.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AudioColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AudioColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var graphRow = new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MaxHeight = analysis.PreferredHeight };
        var optionsRow = new RowDefinition { Height = GridLength.Auto, MinHeight = 48 };
        AudioColumn.RowDefinitions.Add(graphRow);
        AudioColumn.RowDefinitions.Add(optionsRow);
        analysis.RequiredHeightChanged += (_, _) => graphRow.MaxHeight = analysis.PreferredHeight;
        var audioScroll = new ScrollViewer { Content = audio, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AudioSettings = new Expander
        {
            Header = "Audio", Content = audioScroll, IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        AudioSettings.Expanding += (_, _) => optionsRow.Height = new GridLength(1, GridUnitType.Star);
        AudioSettings.Collapsed += (_, _) => optionsRow.Height = AudioSettings.IsExpanded
            ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetRow(audio.GainEditor, 1); Grid.SetRow(analysis, 2); Grid.SetRow(AudioSettings, 3);
        AudioColumn.Children.Add(input); AudioColumn.Children.Add(audio.GainEditor);
        AudioColumn.Children.Add(analysis); AudioColumn.Children.Add(AudioSettings);

        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SceneColumn.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetName(scenePicker, "Scene");
        shaders.Visibility = Visibility.Collapsed;
        Grid.SetRow(shaders, 1);
        SceneColumn.Children.Add(scenePicker); SceneColumn.Children.Add(shaders);
        Grid.SetColumn(SceneColumn, 1);
        Columns.Children.Add(AudioColumn); Columns.Children.Add(SceneColumn);
        var debugButton = new Button { Content = "Debug", HorizontalAlignment = HorizontalAlignment.Right };
        debugButton.Click += (_, _) => debug.Toggle();
        Grid.SetRow(debugButton, 1); Grid.SetColumnSpan(debugButton, 2);
        Columns.Children.Add(debugButton);

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

    public void FocusScene() => scenePicker.Focus(FocusState.Programmatic);

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

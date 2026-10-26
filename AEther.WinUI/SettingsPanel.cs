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
    bool refreshingScenes;
    readonly Grid layout = new() { Padding = new Thickness(16) };
    public string InputName => input.SelectedItem?.ToString() ?? Loopback;
    public string? DeviceError { get; private set; }
    public event EventHandler? InputChanged;
    public event Action<string>? SceneSelected;

    public SettingsPanel(AudioWindow audio, ToolWindow scene, ToolWindow debug, ToolWindow canvas)
    {
        Content = layout;
        layout.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
        var content = new StackPanel { Spacing = 12, Width = 360 };
        content.Children.Add(input);
        content.Children.Add(audio.GainEditor);
        content.Children.Add(scenePicker);
        AutomationProperties.SetName(scenePicker, "Scene");
        scenePicker.SelectionChanged += (_, _) =>
        {
            if (!refreshingScenes && scenePicker.SelectedItem is string name) SceneSelected?.Invoke(name);
        };
        input.Items.Add(Loopback);
        try { foreach (var name in Recorder.GetAvailableDeviceNames()) input.Items.Add(name); }
        catch (Exception error) { DeviceError = error.Message; }
        input.SelectedIndex = 0;
        input.SelectionChanged += (_, _) => InputChanged?.Invoke(this, EventArgs.Empty);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, window) in new[] { ("Canvas", canvas), ("Audio", (ToolWindow)audio), ("Scene", scene), ("Debug", debug) })
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => window.Toggle();
            buttons.Children.Add(button);
        }
        content.Children.Add(buttons);
        layout.Children.Add(content);
    }

    public void SetScenes(IEnumerable<string> names, string selected)
    {
        refreshingScenes = true;
        try
        {
            scenePicker.Items.Clear();
            foreach (var name in names.Order(StringComparer.Ordinal)) scenePicker.Items.Add(name);
            scenePicker.SelectedItem = selected;
            scenePicker.IsEnabled = scenePicker.Items.Count != 0;
        }
        finally { refreshingScenes = false; }
    }
}

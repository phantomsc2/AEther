using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

public sealed class AudioPanel : UserControl
{
    readonly OptionsEditor<AnalyzerOptions> editor;
    public AnalyzerOptions Options => editor.Options;
    public event EventHandler? SettingsChanged;
    public AudioPanel(AnalyzerOptions? options = null)
    {
        editor = new(options?.Copy() ?? new(), value => value.Copy(), new());
        foreach (var group in editor.Content.Children.OfType<Expander>()) group.IsExpanded = true;
        Content = editor.Content;
        editor.Changed += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}

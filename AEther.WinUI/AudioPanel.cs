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
        var cards = new ShaderGroupLayout();
        while (editor.Content.Children.Count > 0)
        {
            var child = editor.Content.Children[0];
            editor.Content.Children.RemoveAt(0);
            cards.Children.Add(ShaderPanel.ExpandCard(child));
        }

        Content = cards;
        editor.Changed += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
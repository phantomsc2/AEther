using AEther.Vortice;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

public sealed class GraphicsPanel : UserControl
{
    readonly OptionsEditor<GraphicsOptions> editor;
    public GraphicsOptions Options => editor.Options;
    public event EventHandler? SettingsChanged;
    public GraphicsPanel()
    {
        editor = new(new GraphicsOptions(), value => value with { }, new());
        Content = editor.Content;
        editor.Changed += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetOptions(GraphicsOptions options) => editor.SetOptions(options);
}

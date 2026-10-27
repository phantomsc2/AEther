using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AEther.WinUI;

public sealed class CanvasWindow : ToolWindow
{
    public event EventHandler? Dismissed;

    public CanvasWindow(UIElement content) : base("", 1280, 720)
    {
        Content = content;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, true);
        presenter.IsResizable = true;
        presenter.IsMaximizable = true;
        presenter.IsMinimizable = true;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            // Windows retains the caption buttons and default top-edge drag region.
            ExtendsContentIntoTitleBar = true;
            var titleBar = AppWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.LightGray;
        }
        AppWindow.Closing += (_, e) =>
        {
            if (e.Cancel) Dismissed?.Invoke(this, EventArgs.Empty);
        };
    }
}

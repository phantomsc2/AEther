using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AEther.WinUI;

public sealed class CanvasWindow : ToolWindow
{
    public CanvasWindow(UIElement content) : base("", 1280, 720)
    {
        Content = content;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true, true);
        presenter.IsResizable = true;
        presenter.IsMaximizable = true;
        presenter.IsMinimizable = true;
    }
}

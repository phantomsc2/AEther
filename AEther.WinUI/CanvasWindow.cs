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
        AppWindow.Closing += (_, e) =>
        {
            if (e.Cancel) Dismissed?.Invoke(this, EventArgs.Empty);
        };
    }

    public void Dismiss()
    {
        Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}

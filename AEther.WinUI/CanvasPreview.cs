using AEther.Vortice;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

public sealed class CanvasPreview : UserControl
{
    readonly SwapChainPanel surface = new();
    readonly TextBlock placeholder = new() { Text = "Canvas not visible", HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center };
    readonly Window owner;
    Renderer? renderer;
    string? error;
    public event EventHandler? OutputChanged;
    public bool CanRender => IsLoaded && owner.AppWindow.IsVisible
        && owner.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }
        && error == null && renderer?.PreviewOutputError == null && renderer?.HasPreviewOutput == true;

    public CanvasPreview(Window owner)
    {
        this.owner = owner;
        AutomationProperties.SetName(surface, "Canvas preview");
        var layout = new Grid();
        layout.Children.Add(surface);
        layout.Children.Add(placeholder);
        Content = layout;
        surface.Loaded += (_, _) => RefreshOutput();
        surface.SizeChanged += (_, _) => RefreshOutput();
        surface.CompositionScaleChanged += (_, _) => RefreshOutput();
        owner.VisibilityChanged += (_, _) => RefreshOutput();
        owner.AppWindow.Changed += (_, _) => OutputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCanvasVisible(bool visible)
    {
        surface.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        placeholder.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Attach(Renderer? value)
    {
        if (ReferenceEquals(renderer, value)) return;
        renderer?.DetachPreviewOutput();
        renderer = value;
        error = null;
        RefreshOutput();
    }

    void RefreshOutput()
    {
        if (renderer != null && owner.AppWindow.IsVisible && surface.ActualWidth > 0 && surface.ActualHeight > 0 && error == null)
        {
            try
            {
                var width = Math.Clamp((int)Math.Round(surface.ActualWidth * surface.CompositionScaleX), 1, 640);
                var height = Math.Clamp((int)Math.Round(surface.ActualHeight * surface.CompositionScaleY), 1, 360);
                if (!renderer.HasPreviewOutput)
                    renderer.AttachPreviewOutput(WinRT.Interop.WindowNative.GetWindowHandle(owner), width, height,
                        chain => SwapChainHost.Attach(surface, chain));
                renderer.ResizePreviewOutput(width, height, (float)(width / surface.ActualWidth), (float)(height / surface.ActualHeight));
            }
            catch (Exception exception)
            {
                error = exception.Message;
                renderer.Logger.ForContext<CanvasPreview>().Error(exception, "Could not attach or resize canvas preview");
            }
        }
        OutputChanged?.Invoke(this, EventArgs.Empty);
    }
}

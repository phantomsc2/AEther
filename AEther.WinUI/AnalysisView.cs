using AEther.Vortice;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AEther.WinUI;

public sealed class AnalysisView : UserControl
{
    readonly SwapChainPanel graph = new();
    readonly Window owner;
    Renderer? renderer;
    string? outputError;
    internal (int Width, int Height) PreviewPixelSize { get; private set; }
    public event EventHandler? OutputChanged;
    public string? Error => outputError ?? renderer?.DebugOutputError;
    public bool CanRender => IsLoaded && owner.AppWindow.IsVisible
        && owner.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized }
        && Error == null && renderer?.HasDebugOutput == true;

    public AnalysisView(Window owner)
    {
        this.owner = owner;
        Content = graph;
        graph.Loaded += (_, _) => RefreshOutput();
        graph.SizeChanged += (_, _) => RefreshOutput();
        graph.CompositionScaleChanged += (_, _) => RefreshOutput();
        owner.VisibilityChanged += (_, _) => RefreshOutput(resetError: true);
        owner.AppWindow.Changed += (_, _) => OutputChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Attach(Renderer? value)
    {
        if (ReferenceEquals(renderer, value)) return;
        if (renderer != null)
        {
            renderer.ShadersChanged -= ShadersChanged;
            renderer.DetachDebugOutput();
        }
        renderer = value;
        if (renderer != null) renderer.ShadersChanged += ShadersChanged;
        outputError = null;
        RefreshOutput();
    }

    void ShadersChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (ReferenceEquals(sender, renderer)) RefreshOutput(resetError: true);
    });

    public void RefreshOutput(bool resetError = false)
    {
        if (resetError) { outputError = null; renderer?.ClearDebugOutputError(); }
        if (renderer != null && owner.AppWindow.IsVisible && graph.ActualWidth > 0 && graph.ActualHeight > 0 && outputError == null)
        {
            try
            {
                // This preview has one vertical value per analysis band. Avoid shading the
                // same bin over many physical pixels, especially on high-DPI/large windows.
                var width = Math.Clamp((int)Math.Round(graph.ActualWidth * graph.CompositionScaleX), 1, 640);
                var height = Math.Clamp((int)Math.Round(graph.ActualHeight * graph.CompositionScaleY), 1,
                    Math.Clamp(renderer.Audio.Bands, 1, 256));
                if (!renderer.HasDebugOutput)
                    renderer.AttachDebugOutput(WinRT.Interop.WindowNative.GetWindowHandle(owner), width, height,
                        chain => SwapChainHost.Attach(graph, chain), lowCost: true);
                renderer.ResizeDebugOutput(width, height, (float)(width / graph.ActualWidth), (float)(height / graph.ActualHeight));
                PreviewPixelSize = (width, height);
            }
            catch (Exception error)
            {
                outputError = error.Message;
                renderer.Logger.ForContext<AnalysisView>().Error(error, "Could not attach or resize analysis preview");
            }
        }
        OutputChanged?.Invoke(this, EventArgs.Empty);
    }
}

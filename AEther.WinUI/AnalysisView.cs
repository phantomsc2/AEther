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
    public event EventHandler? OutputChanged;
    public event EventHandler? RequiredHeightChanged;
    public double PreferredHeight { get; private set; } = 220;
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
        if (renderer != null)
        {
            var height = Math.Max(220, renderer.Audio.Bands * 2);
            if (PreferredHeight != height)
            {
                PreferredHeight = height;
                RequiredHeightChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        if (resetError) { outputError = null; renderer?.ClearDebugOutputError(); }
        if (renderer != null && owner.AppWindow.IsVisible && graph.ActualWidth > 0 && graph.ActualHeight > 0 && outputError == null)
        {
            try
            {
                var width = Math.Max(1, (int)Math.Round(graph.ActualWidth * graph.CompositionScaleX));
                var height = Math.Max(1, (int)Math.Round(graph.ActualHeight * graph.CompositionScaleY));
                if (!renderer.HasDebugOutput)
                    renderer.AttachDebugOutput(WinRT.Interop.WindowNative.GetWindowHandle(owner), width, height,
                        chain => SwapChainHost.Attach(graph, chain));
                renderer.ResizeDebugOutput(width, height, graph.CompositionScaleX, graph.CompositionScaleY);
            }
            catch (Exception error) { outputError = error.Message; }
        }
        OutputChanged?.Invoke(this, EventArgs.Empty);
    }
}

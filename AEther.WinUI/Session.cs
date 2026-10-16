using AEther.Vortice;
using Vortice.DXGI;

namespace AEther.WinUI;

public sealed class Session : IDisposable, IAsyncDisposable
{
    readonly SampleSource source;
    public Analyzer Analyzer { get; }
    public Renderer Renderer { get; }
    Task? stopTask;
    public event EventHandler<Exception?>? CaptureStopped;

    public Session(IntPtr handle, int width, int height, SampleSource source, AnalyzerOptions options,
        string? shaderDirectory = null, Action<IDXGISwapChain1?>? attachSwapChain = null)
    {
        this.source = source;
        Analyzer = new(source.Format, options);
        try
        {
            var rate = source.Format.SampleRate / (double)(source.Format.SampleRate / options.TimeResolution);
            Renderer = new(handle, width, height, options.Domain.Length, rate, options.HistorySeconds, shaderDirectory, attachSwapChain: attachSwapChain);
            Renderer.Audio.Profile = Analyzer.Profile;
        }
        catch { Analyzer.Stop(); throw; } // The caller owns the source until construction succeeds.
        source.DataAvailable += OnAudio;
        source.Stopped += OnStopped;
        Analyzer.SamplesAnalyzed += OnAnalysis;
    }

    void OnAudio(object? sender, ReadOnlySpan<byte> data) => Analyzer.PostSampleSpan(data);
    void OnAnalysis(object? sender, SampleEvent<double> data) => Renderer.PostAudio(data, source.Format.ChannelCount);
    void OnStopped(object? sender, Exception? error) => CaptureStopped?.Invoke(this, error);
    public void Start() { Analyzer.Start(); source.Start(); }
    public Task StopAsync() => stopTask ??= StopCoreAsync();
    async Task StopCoreAsync()
    {
        source.DataAvailable -= OnAudio;
        source.Stopped -= OnStopped;
        try
        {
            try { source.Stop(); }
            finally { Analyzer.Stop(); await Analyzer.Completion; }
        }
        finally { Analyzer.SamplesAnalyzed -= OnAnalysis; }
    }
    public void Dispose() { try { source.Dispose(); } finally { Renderer.Dispose(); } }

    public async ValueTask DisposeAsync()
    {
        // Renderer disposal detaches the WinUI swap chain, so retain the caller's UI context.
        try { await StopAsync(); }
        finally { Dispose(); }
    }
}



using AEther.Vortice;

namespace AEther.WindowsForms;

public sealed class Session : IDisposable
{
    readonly SampleSource source;
    public Analyzer Analyzer { get; }
    public Renderer Renderer { get; }
    Task? stopTask;
    public event EventHandler<Exception?>? CaptureStopped;

    public Session(IntPtr handle, int width, int height, SampleSource source, AnalyzerOptions options)
    {
        this.source = source;
        Analyzer = new(source.Format, options);
        try
        {
            var rate = source.Format.SampleRate / (double)(source.Format.SampleRate / options.TimeResolution);
            Renderer = new(handle, width, height, options.Domain.Length, rate, options.HistorySeconds);
        }
        catch { Analyzer.Stop(); source.Dispose(); throw; }
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
        try { source.Stop(); }
        finally { Analyzer.Stop(); await Analyzer.Completion; }
        Analyzer.SamplesAnalyzed -= OnAnalysis;
    }
    public void Dispose() { source.Dispose(); Renderer.Dispose(); }
}

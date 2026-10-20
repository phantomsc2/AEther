#nullable enable
using AEther.Vortice;
using System;
using System.Threading;
using System.Threading.Tasks;
using Vortice.DXGI;

namespace AEther.WinUI;

public sealed class Session : IDisposable, IAsyncDisposable
{
    readonly SampleSource source;
    public Analyzer Analyzer { get; }
    public Renderer Renderer { get; }
    Task? stopTask;
    int stopping;
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
        if (source is ITimedSampleSource timed) timed.TimedDataAvailable += OnTimedAudio;
        else source.DataAvailable += OnAudio;
        source.Stopped += OnStopped;
        Analyzer.SamplesAnalyzed += OnAnalysis;
    }

    void OnAudio(object? sender, ReadOnlySpan<byte> data) => Analyzer.PostSampleSpan(data,
        new AudioPacketTiming(Quality: AudioTimestampQuality.Estimated));
    void OnTimedAudio(object? sender, ReadOnlySpan<byte> data, AudioPacketTiming timing) => Analyzer.PostSampleSpan(data, timing);
    void OnAnalysis(object? sender, SampleEvent<double> data) => Renderer.PostAudio(data, source.Format.ChannelCount);
    void OnStopped(object? sender, Exception? error) => CaptureStopped?.Invoke(this, error);
    public void Start() { _ = ObserveAnalysisAsync(); Analyzer.Start(); source.Start(); }
    async Task ObserveAnalysisAsync()
    {
        try { await Analyzer.Completion.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (Volatile.Read(ref stopping) == 0) CaptureStopped?.Invoke(this, error);
        }
    }
    public Task StopAsync() => stopTask ??= StopCoreAsync();
    async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref stopping, 1);
        if (source is ITimedSampleSource timed) timed.TimedDataAvailable -= OnTimedAudio;
        else source.DataAvailable -= OnAudio;
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



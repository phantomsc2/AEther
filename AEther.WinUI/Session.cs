#nullable enable
using AEther.Vortice;
using System;
using System.Threading;
using System.Threading.Tasks;
using Vortice.DXGI;

namespace AEther.WinUI;

public sealed class Session : IDisposable, IAsyncDisposable
{
    SampleSource source;
    bool sourceDisposed;
    public Analyzer Analyzer { get; private set; }
    public Renderer Renderer { get; }
    Task? stopTask;
    int stopping;
    public event EventHandler<Exception?>? CaptureStopped;

    public Session(IntPtr handle, int width, int height, SampleSource source, AnalyzerOptions options,
        string? shaderDirectory = null, Action<IDXGISwapChain1?>? attachSwapChain = null, string? initialScene = null)
    {
        this.source = source;
        Analyzer = new(source.Format, options);
        options = Analyzer.Options;
        try
        {
            Renderer = new(handle, width, height, options.GetGraphicsInput(source.Format), shaderDirectory,
                attachSwapChain: attachSwapChain, initialScene: initialScene, profile: Analyzer.Profile);
        }
        catch { Analyzer.Stop(); throw; } // The caller owns the source until construction succeeds.
        Subscribe();
    }

    void Subscribe()
    {
        if (source is ITimedSampleSource timed) timed.TimedDataAvailable += OnTimedAudio;
        else source.DataAvailable += OnAudio;
        source.Stopped += OnStopped;
        Analyzer.SamplesAnalyzed += OnAnalysis;
    }

    /// <summary>Restarts capture and every CPU stage, retaining compiled graphics programs.
    /// The caller owns nextSource until this method succeeds. Call Start afterwards.</summary>
    public async Task RestartAnalysisAsync(SampleSource nextSource, AnalyzerOptions options)
    {
        if (ReferenceEquals(source, nextSource))
            throw new ArgumentException("Use a fresh capture source for the new analysis generation.", nameof(nextSource));
        var next = new Analyzer(nextSource.Format, options);
        try
        {
            await StopAsync();
            Renderer.RestartAudio(next.Options.GetGraphicsInput(nextSource.Format), next.Profile);
            DisposeSource();
        }
        catch
        {
            next.Stop();
            await next.Completion;
            throw;
        }
        source = nextSource;
        sourceDisposed = false;
        Analyzer = next;
        stopTask = null;
        Interlocked.Exchange(ref stopping, 0);
        Subscribe();
    }

    void OnAudio(object? sender, ReadOnlySpan<byte> data) => Analyzer.PostSampleSpan(data,
        new AudioPacketTiming(Quality: AudioTimestampQuality.Estimated));
    void OnTimedAudio(object? sender, ReadOnlySpan<byte> data, AudioPacketTiming timing) => Analyzer.PostSampleSpan(data, timing);
    void OnAnalysis(object? sender, SampleEvent<double> data) => Renderer.PostAudio(data, source.Format.ChannelCount);
    void OnStopped(object? sender, Exception? error) => CaptureStopped?.Invoke(this, error);
    public void Start() { _ = ObserveAnalysisAsync(Analyzer); Analyzer.Start(); source.Start(); }
    async Task ObserveAnalysisAsync(Analyzer analyzer)
    {
        try { await analyzer.Completion.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (Volatile.Read(ref stopping) == 0 && ReferenceEquals(analyzer, Analyzer)) CaptureStopped?.Invoke(this, error);
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
    void DisposeSource()
    {
        if (sourceDisposed) return;
        sourceDisposed = true;
        source.Dispose();
    }
    public void Dispose() { try { DisposeSource(); } finally { Renderer.Dispose(); } }

    public async ValueTask DisposeAsync()
    {
        // Renderer disposal detaches the WinUI swap chain, so retain the caller's UI context.
        try { await StopAsync(); }
        finally { Dispose(); }
    }
}

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
    public Renderer Renderer { get; private set; }
    readonly AnalyzerOptions options;
    readonly double analysisRate;
    Task? stopTask;
    int stopping;
    public event EventHandler<Exception?>? CaptureStopped;

    public Session(IntPtr handle, int width, int height, SampleSource source, AnalyzerOptions options,
        string? shaderDirectory = null, Action<IDXGISwapChain1?>? attachSwapChain = null, string? initialScene = null)
    {
        this.source = source;
        this.options = options;
        Analyzer = new(source.Format, options);
        try
        {
            analysisRate = source.Format.SampleRate / (double)(source.Format.SampleRate / options.TimeResolution);
            Renderer = CreateRenderer(handle, width, height, shaderDirectory, attachSwapChain, initialScene);
            Renderer.Audio.Profile = Analyzer.Profile;
        }
        catch { Analyzer.Stop(); throw; } // The caller owns the source until construction succeeds.
        if (source is ITimedSampleSource timed) timed.TimedDataAvailable += OnTimedAudio;
        else source.DataAvailable += OnAudio;
        source.Stopped += OnStopped;
        Analyzer.SamplesAnalyzed += OnAnalysis;
    }

    // These diagnostic scenes cost less to render on the scanout GPU than to
    // transfer a native-resolution FP16 frame from a separate performance GPU.
    static bool PreferDisplay(string? scene) => scene is "Spectrum" or "Histogram";
    public bool NeedsRendererChange(string scene, IntPtr window) => Renderer.PreferDisplayAdapter != PreferDisplay(scene)
        || (PreferDisplay(scene) && !Renderer.IsDisplayMonitorCurrent(window));

    Renderer CreateRenderer(IntPtr handle, int width, int height, string? shaderDirectory,
        Action<IDXGISwapChain1?>? attachSwapChain, string? scene, AudioHistory? audio = null) =>
        new(handle, width, height, options.Domain.Length, analysisRate, options.HistorySeconds, shaderDirectory,
            attachSwapChain: attachSwapChain, initialScene: scene, preferDisplayAdapter: PreferDisplay(scene), audioHistory: audio);

    // The host stops the render worker first and calls this on the composition UI thread.
    // Analysis continues posting to the same history while graphics resources are replaced.
    public void RecreateRenderer(IntPtr handle, int width, int height,
        Action<IDXGISwapChain1?> attachSwapChain, string scene)
    {
        var audio = Renderer.Audio;
        var directory = Renderer.ShaderDirectory;
        Renderer.Dispose();
        Renderer = CreateRenderer(handle, width, height, directory, attachSwapChain, scene, audio);
        Renderer.Audio.Profile = Analyzer.Profile;
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

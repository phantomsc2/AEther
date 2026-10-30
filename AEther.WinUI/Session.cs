#nullable enable
using AEther.Vortice;
using System;
using System.Threading;
using System.Threading.Tasks;
using Vortice.DXGI;
using Serilog;

namespace AEther.WinUI;

public sealed class Session : IDisposable, IAsyncDisposable
{
    SampleSource source;
    bool sourceDisposed;
    public Analyzer Analyzer { get; private set; }
    public Renderer Renderer { get; }
    Task? stopTask;
    sealed class RunState { public int Stopping, Running; }
    RunState run = new();
    SampleDataHandler? onAudio;
    TimedSampleDataHandler? onTimedAudio;
    EventHandler<SampleEvent<double>>? onAnalysis;
    EventHandler<Exception?>? onStopped;
    public bool IsRunning => Volatile.Read(ref run.Running) != 0;
    public ILogger Logger { get; private set; }
    public long PipelineId { get; private set; }
    public event EventHandler<Exception?>? CaptureStopped;

    public Session(IntPtr handle, int width, int height, SampleSource source, AnalyzerOptions options,
        string? shaderDirectory = null, Action<IDXGISwapChain1?>? attachSwapChain = null, string? initialScene = null,
        GraphicsOptions? graphicsOptions = null, ILogger? logger = null, long pipelineId = 0)
    {
        Logger = (logger ?? Log.Logger).ForContext<Session>();
        PipelineId = pipelineId;
        this.source = source;
        Analyzer = new(source.Format, options);
        options = Analyzer.Options;
        try
        {
            Renderer = new(handle, width, height, options.GetAnalysisOutput(source.Format), shaderDirectory,
                attachSwapChain: attachSwapChain, initialScene: initialScene, profile: Analyzer.Profile, graphicsOptions: graphicsOptions, logger: Logger);
            Renderer.AttachProgress(Analyzer.Progress);
        }
        catch { Analyzer.Stop(); throw; } // The caller owns the source until construction succeeds.
        Subscribe();
        LogConfiguration();
    }

    void LogConfiguration()
    {
        var format = source.Format;
        Logger.Debug("Audio source {SourceType}: {SampleType}, {BitsPerSample} bits per sample, {SampleRate} Hz, {Channels} channels, {BytesPerFrame} bytes per frame; analyzer options {@AnalyzerOptions}",
            source.GetType().FullName, format.Type.GetType().Name, format.Type.Size * 8,
            format.SampleRate, format.ChannelCount, format.Size, Analyzer.Options);
    }

    void Subscribe()
    {
        // Callbacks retain the analyzer, logger and running state of their own generation.
        var analyzer = Analyzer;
        var logger = Logger;
        var state = run;
        var input = source;
        onAudio = (_, data) => analyzer.PostSampleSpan(data, new AudioPacketTiming(Quality: AudioTimestampQuality.Estimated));
        onTimedAudio = (_, data, timing) => analyzer.PostSampleSpan(data, timing);
        onAnalysis = (_, data) => Renderer.PostAudio(data, input.Format.ChannelCount);
        onStopped = (_, error) =>
        {
            if (Volatile.Read(ref state.Stopping) != 0) return;
            Interlocked.Exchange(ref state.Running, 0);
            if (error != null) logger.ForContext("PipelineBlocking", true).Error(error, "Audio capture stopped");
            else logger.Information("Audio capture completed");
            if (ReferenceEquals(state, run)) CaptureStopped?.Invoke(this, error);
        };
        if (source is ITimedSampleSource timed) timed.TimedDataAvailable += onTimedAudio;
        else
        {
            logger.Information("Audio source has no packet timestamps; activating estimated host timing fallback");
            source.DataAvailable += onAudio;
        }
        logger.Debug("Audio capture and analysis callbacks attached; timed source {TimedSource}", source is ITimedSampleSource);
        source.Stopped += onStopped;
        Analyzer.SamplesAnalyzed += onAnalysis;
    }

    /// <summary>Restarts capture and every CPU stage, retaining compiled graphics programs.
    /// The caller owns nextSource until this method succeeds. Call Start afterwards.</summary>
    public async Task RestartAnalysisAsync(SampleSource nextSource, AnalyzerOptions options, ILogger? logger = null, long pipelineId = 0)
    {
        if (ReferenceEquals(source, nextSource))
            throw new ArgumentException("Use a fresh capture source for the new analysis generation.", nameof(nextSource));
        Logger.Debug("Replacing audio analysis generation; retaining graphics programs");
        var next = new Analyzer(nextSource.Format, options);
        try
        {
            await StopAsync();
            Renderer.RestartAudio(next.Options.GetAnalysisOutput(nextSource.Format), next.Profile, logger);
            Renderer.AttachProgress(next.Progress);
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
        Logger = (logger ?? Logger).ForContext<Session>();
        PipelineId = pipelineId;
        stopTask = null;
        run = new();
        Subscribe();
        LogConfiguration();
    }

    public void Start()
    {
        var state = run;
        Interlocked.Exchange(ref state.Running, 1);
        _ = ObserveAnalysisAsync(Analyzer, Logger, state);
        Logger.Debug("Starting analysis workers and audio capture");
        try { Analyzer.Start(); source.Start(); Logger.Debug("Analysis workers and audio capture started"); }
        catch { Interlocked.Exchange(ref state.Running, 0); throw; }
    }
    async Task ObserveAnalysisAsync(Analyzer analyzer, ILogger logger, RunState state)
    {
        try { await analyzer.Completion.ConfigureAwait(false); }
        catch (Exception error)
        {
            if (Volatile.Read(ref state.Stopping) == 0)
            {
                Interlocked.Exchange(ref state.Running, 0);
                logger.ForContext("PipelineBlocking", true).Error(error, "Audio analysis failed");
                if (ReferenceEquals(state, run)) CaptureStopped?.Invoke(this, error);
            }
        }
    }
    public Task StopAsync() => stopTask ??= StopCoreAsync();
    async Task StopCoreAsync()
    {
        Interlocked.Exchange(ref run.Stopping, 1);
        Interlocked.Exchange(ref run.Running, 0);
        Logger.Information("Stopping audio pipeline");
        if (source is ITimedSampleSource timed) timed.TimedDataAvailable -= onTimedAudio;
        else source.DataAvailable -= onAudio;
        source.Stopped -= onStopped;
        try
        {
            try { source.Stop(); }
            finally { Analyzer.Stop(); await Analyzer.Completion; }
        }
        finally { Analyzer.SamplesAnalyzed -= onAnalysis; }
        Logger.Information("Audio pipeline stopped");
    }
    void DisposeSource()
    {
        if (sourceDisposed) return;
        sourceDisposed = true;
        source.Dispose();
        Logger.Debug("Audio source disposed");
    }
    public void Dispose() { try { DisposeSource(); } finally { Renderer.Dispose(); } }

    public async ValueTask DisposeAsync()
    {
        // Renderer disposal detaches the WinUI swap chain, so retain the caller's UI context.
        try { await StopAsync(); }
        finally { Dispose(); }
    }
}

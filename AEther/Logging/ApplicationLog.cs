using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace AEther.Logging;

/// <summary>The application's sole logging configuration. Producers never touch files or XAML.</summary>
public static class ApplicationLog
{
    static readonly object gate = new();
    static bool initialized;
    static int sinkFailureReported;
    static string? emergencyPath;
    public static EventLogStore Events { get; } = new();

    public static void Initialize()
    {
        lock (gate)
        {
            if (initialized) return;
            initialized = true;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AEther", "logs");
            emergencyPath = Path.Combine(directory, $"emergency-{Environment.ProcessId}.log");
            SelfLog.Enable(message => ReportSinkFailure(message));
            Log.Logger = CreateLogger(Events, directory);
            Log.ForContext("SourceContext", "Application").Information(
                "Application started: {ProcessPath}; runtime {Runtime}; OS {OS}; architecture {Architecture}; version {Version}",
                Environment.ProcessPath, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture, Assembly.GetEntryAssembly()?.GetName().Version?.ToString());
        }
    }

    public static Logger CreateLogger(EventLogStore store, string directory)
    {
        var configuration = new LoggerConfiguration().MinimumLevel.Debug()
            .Enrich.WithProperty("ApplicationSessionId", Guid.NewGuid().ToString("N"))
            .WriteTo.Sink(store);
        try
        {
            Directory.CreateDirectory(directory);
            configuration.WriteTo.Async(sink => sink.File(new CompactJsonFormatter(),
                Path.Combine(directory, "aether-.clef"), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14, shared: true), bufferSize: 10000, blockWhenFull: false);
        }
        catch (Exception error)
        {
            // Deliberately bypass the failed file sink; the event history still works.
            using var fallback = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
            fallback.Warning(error, "Application log files are unavailable in {LogDirectory}", directory);
        }
        return configuration.CreateLogger();
    }

    static void ReportSinkFailure(string message)
    {
        Debug.WriteLine(message);
        if (Interlocked.Exchange(ref sinkFailureReported, 1) != 0) return;
        using var fallback = new LoggerConfiguration().WriteTo.Sink(Events).CreateLogger();
        fallback.Warning("Application logging encountered a sink failure: {Details}", message);
    }

    public static void Fatal(string message, Exception? error)
    {
        Log.ForContext("PipelineBlocking", true).Fatal(error, "{Failure}", message);
        // Async sinks may not drain before an unhandled exception terminates the process.
        try
        {
            if (emergencyPath != null)
                File.AppendAllText(emergencyPath, $"{DateTimeOffset.Now:O} {message}\n{error}\n");
        }
        catch { /* Never replace the original fatal exception. */ }
    }

    public static void Shutdown()
    {
        Log.Information("Application stopped");
        Log.CloseAndFlush();
    }
}

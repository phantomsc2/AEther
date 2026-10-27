using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AEther.Logging;
using NUnit.Framework;
using Serilog;
using Serilog.Events;

namespace AEther.Tests;

[TestFixture]
public class LoggingTests
{
    [Test]
    public void EventsRemainInHistoryAcrossPipelineGenerations()
    {
        var store = new EventLogStore();
        using var log = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        var first = store.BeginPipeline();
        var pipeline = log.ForContext("PipelineId", first);
        pipeline.Warning("Overload");
        pipeline.Information("Recovered");
        pipeline.ForContext("PipelineBlocking", true).Error("Capture stopped");
        var next = store.BeginPipeline();
        log.ForContext("PipelineId", next).Warning("SDR fallback during startup");
        pipeline.Error("Late failure");
        Assert.That(next, Is.GreaterThan(first));
        Assert.That(store.Snapshot().Events, Has.Length.EqualTo(5));
    }

    [Test]
    public void HistoryEvictsOldestEvents()
    {
        var store = new EventLogStore(2);
        using var log = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        log.Error("Shader reload failed");
        log.Information("One");
        log.Information("Two");
        Assert.That(store.Snapshot().Events.Select(e => e.Event.RenderMessage()), Is.EqualTo(new[] { "One", "Two" }));
        Assert.That(store.ChangesSince(store.Snapshot().Version), Is.Null);
    }

    [Test]
    public void ConcurrentProducersPreserveBoundedOrderedStructuredHistory()
    {
        var store = new EventLogStore(2000);
        using var log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(store).CreateLogger();
        log.Debug("Not in application history");
        Parallel.For(0, 5000, i => log.Information("Event {Number}", i));
        var rows = store.Snapshot().Events;
        Assert.That(rows, Has.Length.EqualTo(2000));
        Assert.That(rows.Select(r => r.Sequence), Is.EqualTo(Enumerable.Range(3001, 2000).Select(i => (long)i)));
        Assert.That(rows.All(r => r.Event.Properties.ContainsKey("Number")), Is.True);
    }

    [Test]
    public void StructuredFileFlushesAndPreservesExceptionAndMessageTemplate()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "logging-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new EventLogStore();
            using (var log = ApplicationLog.CreateLogger(store, directory))
                log.ForContext("SourceContext", "Probe").Error(new InvalidOperationException("test exception"), "Failure for {Input}", "Microphone");
            var file = Directory.GetFiles(directory, "*.clef").Single();
            using var row = JsonDocument.Parse(File.ReadLines(file).Single());
            Assert.That(row.RootElement.GetProperty("@mt").GetString(), Is.EqualTo("Failure for {Input}"));
            Assert.That(row.RootElement.GetProperty("Input").GetString(), Is.EqualTo("Microphone"));
            Assert.That(row.RootElement.GetProperty("@x").GetString(), Does.Contain("test exception"));
            Assert.That(row.RootElement.GetProperty("ApplicationSessionId").GetString(), Is.Not.Empty);
            Assert.That(store.Snapshot().Events.Single().Event.Exception, Is.TypeOf<InvalidOperationException>());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void UnwritableLogDestinationDoesNotDisableEventHistory()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "blocked-log-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "This file cannot be used as a directory.");
        try
        {
            var store = new EventLogStore();
            using var log = ApplicationLog.CreateLogger(store, path);
            log.Information("Application continues");
            Assert.That(store.Snapshot().Events.Any(e => e.Event.Level == LogEventLevel.Warning), Is.True);
            Assert.That(store.Snapshot().Events.Any(e => e.Event.Exception is IOException), Is.True);
            Assert.That(store.Snapshot().Events.Last().Event.RenderMessage(), Is.EqualTo("Application continues"));
        }
        finally { File.Delete(path); }
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Test]
    public void LossWarningsAggregateAndNormalSilenceOrInitialEstimatedTimingDoNotWarn()
    {
        var clock = new Clock();
        var store = new EventLogStore();
        using var log = new LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        var warnings = new PipelineWarnings(log, clock);
        warnings.Sample(0, 0, 0, AudioTimestampQuality.Estimated);
        clock.Now = clock.Now.AddMinutes(1);
        warnings.Sample(0, 0, 0, AudioTimestampQuality.Estimated);
        Assert.That(store.Snapshot().Events.Any(e => e.Event.Level >= LogEventLevel.Warning), Is.False);
        warnings.Sample(1, 10, 2, AudioTimestampQuality.Device);
        clock.Now = clock.Now.AddSeconds(1);
        warnings.Sample(4, 40, 8, AudioTimestampQuality.Device);
        Assert.That(store.Snapshot().Events.Count(e => e.Event.Level == LogEventLevel.Warning), Is.EqualTo(2));
        clock.Now = clock.Now.AddSeconds(4);
        warnings.Sample(6, 60, 10, AudioTimestampQuality.Estimated);
        var events = store.Snapshot().Events.Where(e => e.Event.Level == LogEventLevel.Warning).ToArray();
        Assert.That(events, Has.Length.EqualTo(5));
        Assert.That(((ScalarValue)events[2].Event.Properties["InputPackets"]).Value, Is.EqualTo(5));
        Assert.That(((ScalarValue)events[3].Event.Properties["VisualRows"]).Value, Is.EqualTo(8));
    }
}

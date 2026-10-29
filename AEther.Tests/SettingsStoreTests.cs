using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AEther.Vortice;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

[TestFixture]
public class SettingsStoreTests
{
    [Test]
    public void RoundTripPersistsAnalyzerAndGraphicsWithoutComputedDomain()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var original = new AppSettings
            {
                Analyzer = new AnalyzerOptions { InputGainDb = -3, UseSwift = false, WindowFunction = DFTWindow.Hamming },
                Graphics = new GraphicsOptions(4, .8, -2.5),
            };
            store.Save(original);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.That(json.RootElement.GetProperty("Analyzer").TryGetProperty("Domain", out _), Is.False);
            Assert.That(json.RootElement.GetProperty("Analyzer").TryGetProperty("SwiftWidthOctaves", out _), Is.False);
            Assert.That(json.RootElement.GetProperty("Analyzer").TryGetProperty("LogAccelerationControl", out _), Is.False);
            Assert.That(json.RootElement.GetProperty("Analyzer").TryGetProperty("ComplexAccelerationControl", out _), Is.False);
            Assert.That(json.RootElement.GetProperty("Analyzer").TryGetProperty("SmoothingNoiseLog2", out _), Is.False);
            Assert.That(json.RootElement.EnumerateObject().Count(), Is.EqualTo(2));
            var loaded = store.Load();
            Assert.That(loaded.Analyzer.InputGainDb, Is.EqualTo(-3));
            Assert.That(loaded.Analyzer.UseSwift, Is.False);
            Assert.That(loaded.Analyzer.WindowFunction, Is.EqualTo(DFTWindow.Hamming));
            Assert.That(loaded.Graphics, Is.EqualTo(original.Graphics));
            store.Save(new AppSettings());
            Assert.That(store.Load().Analyzer.InputGainDb, Is.EqualTo(new AnalyzerOptions().InputGainDb));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Test]
    public void OlderGraphicsSettingsDefaultToZeroExposure()
    {
        var options = JsonSerializer.Deserialize<GraphicsOptions>("{\"HistorySeconds\":4,\"SpectrumAggregation\":0.8}");
        Assert.That(options, Is.EqualTo(new GraphicsOptions(4, .8, 0)));
    }

    [TestCase(-12), TestCase(0), TestCase(12)]
    public void ExposureAcceptsItsFullRange(double exposure) =>
        Assert.DoesNotThrow(() => new GraphicsOptions(Exposure: exposure).Validate());

    [TestCase(-12.01), TestCase(12.01), TestCase(double.NaN), TestCase(double.PositiveInfinity), TestCase(double.NegativeInfinity)]
    public void ExposureRejectsInvalidValues(double exposure) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphicsOptions(Exposure: exposure).Validate());

    [Test]
    public void MissingFileUsesDefaultsAndInvalidSettingsAreRejected()
    {
        var directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "settings-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.That(store.Load().Graphics, Is.EqualTo(new GraphicsOptions()));
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ broken");
            Assert.Throws<JsonException>(() => store.Load());
            File.WriteAllText(path, "{\"Graphics\":{\"HistorySeconds\":-1,\"SpectrumAggregation\":0.5}}");
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Load());
            File.WriteAllText(path, "{\"Analyzer\":null}");
            Assert.Throws<JsonException>(() => store.Load());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

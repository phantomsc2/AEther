using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AEther.Tests;

public class TimingHistoryTests
{
    [Test]
    public void ReadersRetainIndependentPositionsAndReportOverwrittenFrames()
    {
        var profile = new PipelineProfile();
        var history = profile.Track("frame", 3);
        var samples = new List<TimingSample>();
        long first = 0, second = 0;
        profile.Record("frame", 4, 100);
        profile.Record("frame", 8, 200);
        Assert.That(history.ReadSince(ref first, samples), Is.Zero);
        Assert.That(samples.Select(s => s.Timestamp), Is.EqualTo(new long[] { 100, 200 }));
        profile.Record("other", 99, 250);
        profile.Record("frame", 12, 300);
        profile.Record("frame", 16, 400);
        Assert.That(history.ReadSince(ref first, samples), Is.Zero);
        Assert.That(samples.Select(s => s.Milliseconds), Is.EqualTo(new double[] { 12, 16 }));
        Assert.That(history.ReadSince(ref second, samples), Is.EqualTo(1));
        Assert.That(samples.Select(s => s.Timestamp), Is.EqualTo(new long[] { 200, 300, 400 }));
        history.ReadSince(ref first, samples);
        Assert.That(samples, Is.Empty);
        Assert.That(profile.Enabled, Is.False, "Live history must not require CSV profiling.");
    }

    [Test]
    public async Task ConcurrentReaderSeesOrderedSamplesWithoutDuplicates()
    {
        var profile = new PipelineProfile();
        const int count = 10000;
        var history = profile.Track("frame", count);
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < count; i++) profile.Record("frame", i, i);
        });
        var batch = new List<TimingSample>();
        var received = new List<TimingSample>();
        long cursor = 0;
        while (!writer.IsCompleted)
        {
            Assert.That(history.ReadSince(ref cursor, batch), Is.Zero);
            received.AddRange(batch);
            await Task.Yield();
        }
        await writer;
        history.ReadSince(ref cursor, batch);
        received.AddRange(batch);
        Assert.That(received.Select(s => s.Timestamp), Is.EqualTo(Enumerable.Range(0, count).Select(i => (long)i)));
    }
}

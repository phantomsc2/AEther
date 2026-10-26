using System;
using System.Threading;
using System.Threading.Tasks;
using AEther.WinUI;
using NUnit.Framework;

namespace AEther.Tests;

public class RenderWorkerTests
{
    [Test]
    public void EachSwapChainSignalDrawsWithoutUiFrameRequests()
    {
        using var ready = new AutoResetEvent(false);
        using var rendered = new AutoResetEvent(false);
        Exception failure = null;
        using var worker = new RenderWorker(ready, () => rendered.Set(), error => failure = error);
        worker.SetEnabled(true);
        Assert.That(rendered.WaitOne(50), Is.False, "Wait before the first frame too.");
        for (int i = 0; i < 8; i++)
        {
            ready.Set();
            Assert.That(rendered.WaitOne(2000), Is.True, "DXGI alone must drive subsequent frames.");
            Assert.That(rendered.WaitOne(20), Is.False, "Do not draw again before DXGI is ready.");
        }
        Assert.That(failure, Is.Null);
    }

    [Test]
    public void PauseAndResumePreservePendingPresentation()
    {
        using var ready = new AutoResetEvent(true);
        using var rendered = new AutoResetEvent(false);
        using var worker = new RenderWorker(ready, () => rendered.Set(), _ => { });
        Assert.That(rendered.WaitOne(50), Is.False, "Start paused until the host enables rendering.");
        worker.SetEnabled(true);
        Assert.That(rendered.WaitOne(2000), Is.True);
        worker.SetEnabled(false);
        ready.Set();
        Assert.That(rendered.WaitOne(50), Is.False, "A minimized window must not render.");
        worker.SetEnabled(true);
        Assert.That(rendered.WaitOne(2000), Is.True, "Restore must retain the pending DXGI signal.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void ShutdownInterruptsBothPauseAndPresentationWaits(bool enabled)
    {
        using var ready = new AutoResetEvent(false);
        var worker = new RenderWorker(ready, () => { }, _ => { });
        worker.SetEnabled(enabled);
        var shutdown = Task.Run(worker.Dispose);
        Assert.That(shutdown.Wait(2000), Is.True, "Shutdown must not require another display refresh.");
        worker.Dispose();
        Assert.That(ready.SafeWaitHandle.IsClosed, Is.False, "The renderer owns the DXGI handle.");
    }

    [Test]
    public void CompositorTickThenSwapChainReadinessGateSamplingWithoutRequiringAnotherTick()
    {
        using var ready = new AutoResetEvent(false);
        using var tick = new AutoResetEvent(false);
        using var waiting = new AutoResetEvent(false);
        using var rendered = new AutoResetEvent(false);
        Exception failure = null;
        using var worker = new RenderWorker(ready, () => rendered.Set(), error => failure = error,
            waitForTick: stopped => { waiting.Set(); return WaitHandle.WaitAny([stopped, tick]) == 1; });
        worker.SetEnabled(true);
        Assert.That(waiting.WaitOne(2000), Is.True);
        Assert.That(rendered.WaitOne(30), Is.False, "Do not render before the compositor tick.");
        tick.Set();
        Assert.That(rendered.WaitOne(30), Is.False, "A compositor tick does not make an occupied back buffer ready.");
        ready.Set();
        Assert.That(rendered.WaitOne(2000), Is.True);
        Assert.That(waiting.WaitOne(2000), Is.True);
        Assert.That(rendered.WaitOne(30), Is.False, "Each subsequent frame needs another tick.");
        ready.Set();
        Assert.That(rendered.WaitOne(30), Is.False, "Early swap-chain readiness must not bypass the compositor cadence.");
        tick.Set();
        Assert.That(rendered.WaitOne(2000), Is.True, "A ready swap chain renders on this tick, without waiting for another.");
        Assert.That(Task.Run(worker.Dispose).Wait(2000), Is.True, "Stop must interrupt a compositor wait too.");
        Assert.That(failure, Is.Null);
    }

    [Test]
    public void ShutdownInterruptsSwapChainBackpressureAfterACompositorTick()
    {
        using var ready = new AutoResetEvent(false);
        using var tick = new AutoResetEvent(true);
        using var passedTick = new AutoResetEvent(false);
        int draws = 0;
        using var worker = new RenderWorker(ready, () => Interlocked.Increment(ref draws), _ => { },
            waitForTick: stopped =>
            {
                bool result = WaitHandle.WaitAny([stopped, tick]) == 1;
                if (result) passedTick.Set();
                return result;
            });
        worker.SetEnabled(true);
        Assert.That(passedTick.WaitOne(2000), Is.True);
        Assert.That(Task.Run(worker.Dispose).Wait(2000), Is.True);
        Assert.That(draws, Is.Zero);
    }

    [Test]
    public void RenderFailureIsReportedAndStopsTheWorker()
    {
        using var ready = new AutoResetEvent(true);
        using var reported = new AutoResetEvent(false);
        var expected = new InvalidOperationException("render failed");
        Exception failure = null;
        int draws = 0;
        using var worker = new RenderWorker(ready, () => { Interlocked.Increment(ref draws); throw expected; },
            error => { failure = error; reported.Set(); });
        worker.SetEnabled(true);
        Assert.That(reported.WaitOne(2000), Is.True);
        ready.Set();
        worker.Dispose();
        Assert.That(failure, Is.SameAs(expected));
        Assert.That(draws, Is.EqualTo(1));
    }
}

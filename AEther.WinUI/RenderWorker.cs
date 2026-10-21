#nullable enable

using System;
using System.Diagnostics;
using System.Threading;

namespace AEther.WinUI;

/// <summary>Draws on a dedicated thread paced by the compositor or swap chain, independently of XAML.</summary>
internal sealed class RenderWorker : IDisposable
{
    readonly ManualResetEvent enabled = new(false);
    readonly ManualResetEvent stopped = new(false);
    readonly WaitHandle frameReady;
    readonly Action render;
    readonly Action<Exception> failed;
    readonly Func<WaitHandle, bool>? waitForTick;
    readonly Action<double>? waited;
    readonly Thread thread;
    int disposed;

    public RenderWorker(WaitHandle frameReady, Action render, Action<Exception> failed,
        Func<WaitHandle, bool>? waitForTick = null, Action<double>? waited = null)
    {
        this.frameReady = frameReady;
        this.render = render;
        this.failed = failed;
        this.waitForTick = waitForTick;
        this.waited = waited;
        thread = new Thread(Run) { IsBackground = true, Name = "AEther render" };
        thread.Start();
    }

    public void SetEnabled(bool value)
    {
        if (value) enabled.Set();
        else enabled.Reset();
    }

    void Run()
    {
        try
        {
            WaitHandle[] active = [stopped, enabled];
            WaitHandle[] ready = [stopped, frameReady];
            while (WaitHandle.WaitAny(active) == 1)
            {
                var start = Stopwatch.GetTimestamp();
                // Each source supplies a complete cadence. Waiting for both serially
                // can miss a compositor tick and halve the native-resolution rate.
                if (waitForTick != null)
                {
                    if (!waitForTick(stopped)) return;
                }
                else if (WaitHandle.WaitAny(ready) == 0) return;
                // Keep a consumed frame signal if minimized while waiting for DXGI.
                // Resume can then draw without needing another presentation first.
                if (WaitHandle.WaitAny(active) == 0) return;
                waited?.Invoke(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                render();
            }
        }
        catch (Exception error) { failed(error); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopped.Set();
        if (Thread.CurrentThread != thread) thread.Join();
        enabled.Dispose();
        stopped.Dispose();
    }
}

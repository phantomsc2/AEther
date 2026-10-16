namespace AEther.WinUI;

/// <summary>Consumes at most one queued composition frame on a dedicated thread.</summary>
internal sealed class RenderWorker : IDisposable
{
    readonly AutoResetEvent frame = new(false);
    readonly Action render;
    readonly Action<Exception> failed;
    readonly Thread thread;
    volatile bool stopping;

    public RenderWorker(Action render, Action<Exception> failed)
    {
        this.render = render;
        this.failed = failed;
        thread = new Thread(Run) { IsBackground = true, Name = "AEther render" };
        thread.Start();
    }

    public void RequestFrame()
    {
        if (!stopping)
        {
            try { frame.Set(); }
            catch (ObjectDisposedException) { }
        }
    }

    void Run()
    {
        while (true)
        {
            frame.WaitOne();
            if (stopping) return;
            try { render(); }
            catch (Exception error)
            {
                stopping = true;
                failed(error);
                return;
            }
        }
    }

    public void Dispose()
    {
        stopping = true;
        frame.Set();
        if (Thread.CurrentThread != thread) thread.Join();
        frame.Dispose();
    }
}

using System;

namespace AEther
{
    public delegate void SampleDataHandler(object? sender, ReadOnlySpan<byte> data);

    public abstract class SampleSource : IDisposable
    {

        /// <summary>Data is valid only during the callback. Copy before returning for asynchronous use.</summary>
        public SampleDataHandler? DataAvailable;
        public EventHandler<Exception?>? Stopped;

        public abstract SampleFormat Format { get; }
        public abstract void Start();
        public abstract void Stop();
        public abstract void Dispose();

    }
}


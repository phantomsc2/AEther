using NAudio.Wave;
using System;

namespace AEther.NAudio
{
    public class Loopback : Input
    {
        public Loopback(int requestedBufferMilliseconds = 1)
            : base(CreateDevice(requestedBufferMilliseconds))
        { }

        static WasapiRecorder CreateDevice(int requestedBufferMilliseconds)
        {
            var builder = new WasapiRecorderBuilder()
                .WithLoopbackCapture()
                .WithBufferLength(Math.Clamp(requestedBufferMilliseconds, 1, 100))
                .WithMmcssThreadPriority("Pro Audio");
            // Event-driven loopback requires Windows 10 1703. IAudioClient3
            // WithLowLatency is not supported for loopback; keep the mix format
            // and request the smallest shared buffer instead. Windows rounds it
            // to the engine's supported duration.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
                builder.WithEventSync();
            else
                builder.WithPollingSync();
            return builder.Build();
        }
    }
}


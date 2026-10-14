
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System;

namespace AEther.NAudio
{

    public class Loopback : Input
    {

        public Loopback(int requestedBufferMilliseconds = 1)
            : base(new LowLatencyLoopbackCapture(requestedBufferMilliseconds))
        { }

        // NAudio 2.2.1's WasapiLoopbackCapture exposes neither the buffer
        // duration nor event synchronization; its base defaults to 100 ms
        // and polls after sleeping for half the actual allocated buffer.
        sealed class LowLatencyLoopbackCapture : WasapiCapture
        {
            public LowLatencyLoopbackCapture(int requestedBufferMilliseconds)
                : base(WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice(),
                    OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063),
                    Math.Clamp(requestedBufferMilliseconds, 1, 100))
            { }

            protected override AudioClientStreamFlags GetAudioClientStreamFlags()
                => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
        }

    }
}

#nullable enable
using System;
using NAudio.Wave;
using NUnit.Framework;

namespace AEther.Tests
{
    public class CaptureFormatTests
    {
        [Test]
        public void ExtensibleFloatMixFormatPreservesRateAndChannels()
        {
            var format = AEther.NAudio.Input.ToSampleFormat(new WaveFormatExtensible(48000, 32, 6));
            Assert.That(format.Type, Is.SameAs(SampleType.Float32.Instance));
            Assert.That(format.SampleRate, Is.EqualTo(48000));
            Assert.That(format.ChannelCount, Is.EqualTo(6));
        }

        [Test]
        public void UnsupportedPcmDepthIsRejected()
        {
            Assert.Throws<NotSupportedException>(() =>
                AEther.NAudio.Input.ToSampleFormat(new WaveFormat(48000, 24, 2)));
        }

        [Test]
        public void UnsupportedDeviceFormatDisposesDeviceWithoutLeavingSubscribers()
        {
            var device = new FakeCapture(new WaveFormat(48000, 24, 2));

            Assert.Throws<NotSupportedException>(() => new TestInput(device));

            Assert.That(device.Disposed, Is.True);
            Assert.That(device.SubscriberCount, Is.Zero);
        }

        [Test]
        public void SupportedDeviceForwardsOnlyRecordedBytesAndIsOwnedByInput()
        {
            var device = new FakeCapture(new WaveFormat(48000, 16, 2));
            var input = new TestInput(device);
            byte[]? received = null;
            input.DataAvailable += (_, data) => received = data.ToArray();

            device.Deliver(new byte[] { 1, 2, 3, 4, 99 }, 4);

            Assert.That(received, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(device.Disposed, Is.False);
            input.Dispose();
            Assert.That(device.Disposed, Is.True);
        }

        sealed class TestInput(IWaveIn device) : AEther.NAudio.Input(device);

        sealed class FakeCapture(WaveFormat format) : IWaveIn
        {
            public WaveFormat WaveFormat { get; set; } = format;
            public bool Disposed { get; private set; }
            public event EventHandler<WaveInEventArgs>? DataAvailable;
            public event EventHandler<StoppedEventArgs>? RecordingStopped;
            public int SubscriberCount => (DataAvailable?.GetInvocationList().Length ?? 0)
                + (RecordingStopped?.GetInvocationList().Length ?? 0);
            public void Deliver(byte[] data, int count)
                => DataAvailable?.Invoke(this, new WaveInEventArgs(data, count));
            public void StartRecording() { }
            public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
            public void Dispose() => Disposed = true;
        }
    }
}

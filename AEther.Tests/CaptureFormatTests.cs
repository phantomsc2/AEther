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
    }
}

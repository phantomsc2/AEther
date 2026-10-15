using NAudio.Wave;
using System;

namespace AEther.NAudio
{
    public abstract class Input : SampleSource
    {

        public override SampleFormat Format { get; }

        protected readonly IWaveIn? Device;
        protected readonly WasapiRecorder? WasapiDevice;
        WaveFormat WaveFormat => WasapiDevice?.WaveFormat ?? Device!.WaveFormat;

        public Input(IWaveIn device)
        {
            Device = device;
            Device.DataAvailable += Device_DataAvailable;
            Device.RecordingStopped += Device_RecordingStopped;
            Format = ToSampleFormat(device.WaveFormat);
        }

        protected Input(WasapiRecorder device)
        {
            WasapiDevice = device;
            Format = ToSampleFormat(device.WaveFormat);
            device.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
                DataAvailable?.Invoke(this, buffer);
            device.RecordingStopped += Device_RecordingStopped;
        }
        private void Device_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            Stopped?.Invoke(this, e.Exception);
        }

        private void Device_DataAvailable(object? sender, WaveInEventArgs evt)
        {
            var data = evt.Buffer.AsSpan(0, evt.BytesRecorded);
            DataAvailable?.Invoke(this, data);
        }

        public void Playback()
        {
            var wo = new WaveOut();
            var bwp = new BufferedWaveProvider(WaveFormat)
            {
                DiscardOnBufferOverflow = true,
            };
            DataAvailable += (sender, data) =>
            {
                bwp.AddSamples(data);
            };
            Stopped += (sender, error) =>
            {
                wo.Stop();
                wo.Dispose();
            };

            wo.Init(bwp);
            wo.Play();
        }

        public static SampleFormat ToSampleFormat(WaveFormat format)
        {
            if (format is WaveFormatExtensible extensible)
                format = extensible.ToStandardWaveFormat();
            SampleType type = (format.Encoding, format.BitsPerSample) switch
            {
                (WaveFormatEncoding.Pcm, 16) => SampleType.UInt16.Instance,
                (WaveFormatEncoding.IeeeFloat, 32) => SampleType.Float32.Instance,
                _ => throw new NotSupportedException($"Unsupported capture format: {format}"),
            };
            return new SampleFormat(type, format.SampleRate, format.Channels);
        }

        public override void Start()
        {
            if (WasapiDevice != null) WasapiDevice.StartRecording();
            else Device!.StartRecording();
        }

        public override void Stop()
        {
            if (WasapiDevice != null) WasapiDevice.StopRecording();
            else Device!.StopRecording();
        }

        public override void Dispose()
        {
            WasapiDevice?.Dispose();
            Device?.Dispose();
            GC.SuppressFinalize(this);
        }

    }
}


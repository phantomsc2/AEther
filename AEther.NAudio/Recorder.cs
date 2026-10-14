
using NAudio.Wave;
using System;
using System.Linq;

namespace AEther.NAudio
{
    public class Recorder : Input
    {

        public Recorder(string deviceName)
            : base(CreateDevice(deviceName))
        { }

        public static string[] GetAvailableDeviceNames()
        {
            return Enumerable.Range(0, WaveIn.DeviceCount)
                .Select(i => WaveIn.GetCapabilities(i))
                .Select(d => d.ProductName)
                .ToArray();
        }

        static WaveIn CreateDevice(string deviceName)
        {
            var devices = GetAvailableDeviceNames();
            var deviceIndex = Array.FindIndex(devices, d => d == deviceName);
            return new WaveIn()
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(48000, 16, 2),
                BufferMilliseconds = 10,
                NumberOfBuffers = 3,
            };
        }

    }
}

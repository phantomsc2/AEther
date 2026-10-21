using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace AEther.Vortice;

public sealed partial class Renderer
{
    // Internal reference path for correctness comparisons and upload measurements.
    internal bool FullHistoryUploads { get; set; }
    internal readonly record struct AudioUpload(int Rows, long Bytes, int Calls, double Milliseconds);
    // Counts cover both histogram textures; Rows is the number updated per channel.
    internal AudioUpload LastHistoryUpload { get; private set; }

    unsafe void UploadAudio()
    {
        long start = Stopwatch.GetTimestamp();
        // Capture/analysis never touches a graphics lock. All uploads follow Advance.
        context.PSSetShaderResources(0, ShaderProgram.EmptyViews);
        context.VSSetShaderResources(0, ShaderProgram.EmptyViews);
        uint pitch = (uint)(Audio.Bands * 16);
        for (int channel = 0; channel < 2; channel++)
            fixed (float* data = Audio.Spectrum[channel])
                context.UpdateSubresource(textures[channel], 0, null, (IntPtr)data, pitch, 0);

        Span<AudioHistory.HistoryRange> ranges = stackalloc AudioHistory.HistoryRange[2];
        int count = Audio.GetHistoryUpdates(ranges);
        if (FullHistoryUploads) { ranges[0] = new(0, Audio.Rows); count = 1; }
        int rows = 0;
        for (int i = 0; i < count; i++)
        {
            var range = ranges[i];
            Box? box = FullHistoryUploads ? null
                : new Box(0, range.Start, 0, Audio.Bands, range.Start + range.Count, 1);
            for (int channel = 0; channel < 2; channel++)
                fixed (float* data = Audio.History[channel])
                    context.UpdateSubresource(textures[channel + 2], 0, box,
                        (IntPtr)(data + range.Start * Audio.Bands * 4), pitch, 0);
            rows += range.Count;
        }
        Audio.AcknowledgeHistoryUpload();
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LastHistoryUpload = new(rows, (long)rows * pitch * 2, count * 2, milliseconds);
        Audio.Profile?.Record("render.audio_upload_ms", milliseconds);
    }

    // GPU readback is used only by validation, never by the live upload path.
    internal float[] ReadHistogram(int channel)
    {
        if ((uint)channel >= 2) throw new ArgumentOutOfRangeException(nameof(channel));
        lock (graphicsGate)
        {
            var texture = textures[channel + 2];
            var desc = texture.Description;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device.CreateTexture2D(desc);
            context.CopyResource(staging, texture);
            var map = context.Map(staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                var result = new float[Audio.Rows * Audio.Bands * 4];
                for (int row = 0; row < Audio.Rows; row++)
                    Marshal.Copy(map.DataPointer + row * (int)map.RowPitch, result,
                        row * Audio.Bands * 4, Audio.Bands * 4);
                return result;
            }
            finally { context.Unmap(staging, 0); }
        }
    }
}

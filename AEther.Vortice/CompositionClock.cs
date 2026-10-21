using System.Runtime.InteropServices;

namespace AEther.Vortice;

/// <summary>Reads the compositor's estimate for the next displayed frame in the QPC clock domain.</summary>
internal sealed class CompositionClock : IDisposable
{
    IntPtr device;

    public CompositionClock()
    {
        // A timing-only DirectComposition device needs no DXGI device or visual tree.
        var iid = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
        DCompositionCreateDevice(IntPtr.Zero, in iid, out device);
    }

    public unsafe long? NextFrameTimestamp()
    {
        if (device == IntPtr.Zero) return null;
        var vtable = *(IntPtr**)device;
        var getStatistics = (delegate* unmanaged[Stdcall]<IntPtr, Statistics*, int>)vtable[5];
        Statistics statistics = default;
        if (getStatistics(device, &statistics) < 0 || statistics.Frequency <= 0
            || statistics.NextFrame <= 0) return null;
        return AudioClock.ConvertTimestamp(statistics.NextFrame, statistics.Frequency);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Statistics
    {
        public long LastFrame;
        public uint RateNumerator, RateDenominator;
        public long CurrentTime, Frequency, NextFrame;
    }

    public void Dispose()
    {
        if (device != IntPtr.Zero) { Marshal.Release(device); device = IntPtr.Zero; }
    }

    [DllImport("dcomp.dll", ExactSpelling = true)]
    static extern int DCompositionCreateDevice(IntPtr dxgiDevice, in Guid iid, out IntPtr device);
}

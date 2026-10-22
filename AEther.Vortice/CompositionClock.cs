using System.Runtime.InteropServices;

namespace AEther.Vortice;

/// <summary>Reads the compositor's estimate for the next displayed frame in the QPC clock domain.</summary>
internal sealed class CompositionClock : IDisposable
{
    IntPtr device;
    readonly PresentationTimeline timeline = new();
    long? previousRaw;

    public CompositionClock()
    {
        // A timing-only DirectComposition device needs no DXGI device or visual tree.
        var iid = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
        DCompositionCreateDevice(IntPtr.Zero, in iid, out device);
    }

    public unsafe long? NextFrameTimestamp(PipelineProfile? profile = null, bool stabilize = true)
    {
        if (device == IntPtr.Zero) return null;
        var vtable = *(IntPtr**)device;
        var getStatistics = (delegate* unmanaged[Stdcall]<IntPtr, Statistics*, int>)vtable[5];
        Statistics statistics = default;
        if (getStatistics(device, &statistics) < 0 || statistics.Frequency <= 0
            || statistics.NextFrame <= 0) return null;
        long raw = AudioClock.ConvertTimestamp(statistics.NextFrame, statistics.Frequency);
        if (previousRaw is { } previous)
            profile?.Record("render.raw_display_interval_ms", AudioClock.Seconds(raw - previous) * 1000);
        previousRaw = raw;
        if (!stabilize || statistics.RateNumerator == 0 || statistics.RateDenominator == 0) return raw;
        long now = AudioClock.ConvertTimestamp(statistics.CurrentTime, statistics.Frequency);
        double period = System.Diagnostics.Stopwatch.Frequency * (double)statistics.RateDenominator / statistics.RateNumerator;
        return timeline.Estimate(now, raw, period);
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

/// <summary>Keep a compositor-derived phase while ignoring isolated repeated/skipped estimates.</summary>
internal sealed class PresentationTimeline
{
    long origin, anchor, lastNow, last;
    double period;
    bool initialized;

    internal long Estimate(long now, long estimate, double framePeriod)
    {
        // New refresh modes and restore after a pause establish a new correlation.
        // Ordinary scheduling jitter must not reanchor the animation clock.
        if (!initialized || Math.Abs(framePeriod - period) > period * .001
            || now < lastNow || AudioClock.Seconds(now - lastNow) > .5)
        {
            origin = lastNow = now;
            anchor = last = estimate;
            period = framePeriod;
            initialized = true;
            return estimate;
        }
        lastNow = now;
        long frame = (long)Math.Round((now - origin) / period);
        long target = anchor + (long)Math.Round(frame * period);
        last = Math.Max(last, target);
        return last;
    }
}

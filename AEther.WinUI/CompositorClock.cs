using System.Runtime.InteropServices;

namespace AEther.WinUI;

/// <summary>The Windows 11 composition heartbeat supports mixed-refresh displays and cancellation.</summary>
internal static class CompositorClock
{
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static bool Wait(WaitHandle stopped)
    {
        // The worker owns stopped and keeps it alive until its thread has joined.
        var handle = stopped.SafeWaitHandle.DangerousGetHandle();
        while (true)
        {
            uint result = DCompositionWaitForCompositorClock(1, ref handle, 1000);
            if (result == 0) return false; // Our stop event.
            if (result == 1) return true;  // WAIT_OBJECT_0 + handle count: compositor tick.
            // Display-off/occlusion and timeouts must neither spin nor prevent shutdown.
            if (stopped.WaitOne(100)) return false;
        }
    }

    [DllImport("dcomp.dll", ExactSpelling = true)]
    static extern uint DCompositionWaitForCompositorClock(uint count, ref IntPtr handles, uint timeoutInMs);
}

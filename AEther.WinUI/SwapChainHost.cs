using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.DXGI;

namespace AEther.WinUI;

/// <summary>The WinUI 3 interface (distinct from the UWP SwapChainPanel interface).</summary>
internal static class SwapChainHost
{
    static readonly Guid PanelInterface = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    public static unsafe void Attach(SwapChainPanel panel, IDXGISwapChain1? swapChain)
    {
        // CsWinRT's marshaler supplies an owned IUnknown; the panel retains the chain
        // after SetSwapChain. Query and release the native interface for every call.
        var unknown = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        IntPtr native = IntPtr.Zero;
        try
        {
            var iid = PanelInterface;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out native));
            var vtable = *(IntPtr**)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain?.NativePointer ?? IntPtr.Zero));
        }
        finally
        {
            if (native != IntPtr.Zero) Marshal.Release(native);
            Marshal.Release(unknown);
        }
    }
}

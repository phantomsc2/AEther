using System;
using Win32Exception = System.ComponentModel.Win32Exception;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;
using Vortice.DXGI;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public class SwapChainPacingTests
{
    [TestCase(false), TestCase(true), Category("GPU")]
    public void CompositionPacingSurvivesResizeAndClosesWithRenderer(bool highPrecision)
    {
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var shaders = Path.Combine(temporaryRoot, "AEther-pacing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(shaders, "Common"));
        Directory.CreateDirectory(Path.Combine(shaders, "Scenes"));
        try
        {
            var source = FindShaders();
            foreach (var name in new[] { "Inputs.hlsl", "Present.hlsl" })
                File.Copy(Path.Combine(source, "Common", name), Path.Combine(shaders, "Common", name));
            File.WriteAllText(Path.Combine(shaders, "Scenes", "Spectrum.hlsl"),
                "float4 PS(PSin input) : SV_Target { return float4(0, 0, 0, 1); }");
            using var window = new HiddenWindow();
            IDXGISwapChain1 attached = null;
            using var renderer = new Renderer(window.Handle, 1280, 720, 1, 60,
                shaderDirectory: shaders, highPrecisionOutput: highPrecision, attachSwapChain: chain => attached = chain);
            Assert.That(attached, Is.Not.Null);
            Assert.That(attached.Description1.Flags.HasFlag(SwapChainFlags.FrameLatencyWaitableObject), Is.True);
            using (var chain = attached.QueryInterface<IDXGISwapChain2>())
                Assert.That(chain.MaximumFrameLatency, Is.EqualTo(1));
            var frameReady = renderer.FrameReady;
            var handle = frameReady.SafeWaitHandle;
            Assert.That(frameReady.WaitOne(2000), Is.True, "The initial frame must be ready before Present.");
            renderer.SelectScene("Spectrum");
            renderer.SetCompositionScale(1.25f, 1.25f);
            renderer.Render(false);
            renderer.Resize(1366, 768);
            renderer.Render(false);
            Assert.That(renderer.ReadPixels(false).Length, Is.EqualTo(1366 * 768 * 4));
            Assert.That(attached.Description1.Flags.HasFlag(SwapChainFlags.FrameLatencyWaitableObject), Is.True);
            Assert.That(renderer.FrameReady, Is.SameAs(frameReady));
            renderer.Dispose();
            Assert.That(attached, Is.Null, "Detach the composition host during disposal.");
            Assert.That(handle.IsClosed, Is.True, "Close the native DXGI wait handle.");
        }
        finally
        {
            if (!Path.GetFullPath(shaders).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Pacing fixture cleanup escaped the temporary directory.");
            Directory.Delete(shaders, true);
        }
    }

    static string FindShaders()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "AEther.Vortice", "Shaders");
            if (Directory.Exists(Path.Combine(path, "Scenes"))) return path;
        }
        throw new DirectoryNotFoundException("Cannot locate the shader source tree.");
    }

    sealed class HiddenWindow : IDisposable
    {
        public IntPtr Handle { get; } = CreateWindowEx(0, "STATIC", "AEther swap-chain pacing regression",
            0x80000000, 0, 0, 1280, 720, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        public HiddenWindow()
        {
            if (Handle == IntPtr.Zero) throw new Win32Exception();
        }
        public void Dispose() => DestroyWindow(Handle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll")]
        static extern bool DestroyWindow(IntPtr handle);
    }
}

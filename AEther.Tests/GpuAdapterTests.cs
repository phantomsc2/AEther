#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;
using Vortice.DXGI;

namespace AEther.Tests;

[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public class GpuAdapterTests
{
    [TestCase(0x8086u, "Intel(R) Arc(TM) Graphics", false, true)]
    [TestCase(0x8086u, "Intel Arc A770 Graphics", false, true)]
    [TestCase(0x8086u, "Intel ARC Graphics", false, true)]
    [TestCase(0x8086u, "Intel Iris Xe Graphics", false, false)]
    [TestCase(0x10deu, "NVIDIA GeForce RTX 4070 Laptop GPU", false, false)]
    [TestCase(0x1414u, "Microsoft Basic Render Driver", true, false)]
    [TestCase(0x8086u, "Intel Arc", true, false)]
    public void ArcIdentificationUsesVendorAndHardwareIdentity(uint vendor, string name, bool software, bool expected) =>
        Assert.That(Renderer.IsArcAdapter(vendor, name, software), Is.EqualTo(expected));

    [Test, Category("GPU")]
    public void DefaultRendererUsesArcWheneverPresent()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        string? arc = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                var d = adapter.Description1;
                if (Renderer.IsArcAdapter(d.VendorId, d.Description, (d.Flags & AdapterFlags.Software) != 0))
                { arc = d.Description; break; }
            }
        }
        if (arc == null) Assert.Ignore("Arc is absent; this machine uses the standard hardware fallback.");
        var window = CreateWindowEx(0, "STATIC", "AEther Arc selection regression", 0x80000000,
            0, 0, 1280, 720, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try
        {
            using var renderer = new Renderer(window, 1280, 720, 1, 100, initialScene: "Histogram");
            Assert.That(renderer.AdapterDescription, Is.EqualTo(arc));
            TestContext.Out.WriteLine($"Global default: {renderer.AdapterDescription}");
            foreach (var scene in renderer.Scenes)
            {
                Assert.DoesNotThrow(() => renderer.SelectScene(scene.Name), scene.Name);
                Assert.That(renderer.AdapterDescription, Is.EqualTo(arc), scene.Name);
                TestContext.Progress.WriteLine($"Compiled on Arc: {scene.Name}");
            }
        }
        finally { DestroyWindow(window); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr window);
}

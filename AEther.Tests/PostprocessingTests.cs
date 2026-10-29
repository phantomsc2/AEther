using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using AEther.Vortice;
using NUnit.Framework;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [TestCase(false), TestCase(true), Category("GPU")]
    public void ExposureMapsOnlyLinearPixelsAndExportRetainsTheLastFramesExposure(bool highPrecision)
    {
        using var shaders = new SceneShaders("Spectrum");
        File.WriteAllText(Path.Combine(shaders.Path, "Scenes", "Spectrum.hlsl"), """
            float4 PS(PSin input) : SV_Target
            {
                float3 color = float3(.001, .125, .5);
                return input.UV.y < .5 ? LinearOutput(color) : DisplayOutput(color);
            }
            """);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 32, 18, 8, 60,
            shaderDirectory: shaders.Path, timeProvider: new RenderClock(), highPrecisionOutput: highPrecision);
        var passes = new List<string>();
        renderer.TransportPassMarker = (name, begin) => { if (begin) passes.Add(name); };
        renderer.Render(false);
        byte[] original = renderer.ReadPixels(false);
        byte[] dithered = renderer.ReadPixels();
        Assert.That(renderer.ReadPixels(), Is.EqualTo(dithered), "Dither must be spatially stable.");
        Assert.That(dithered.Zip(original, (a, b) => Math.Abs(a - b)).Max(), Is.LessThanOrEqualTo(1));
        if (!highPrecision)
            Assert.That(renderer.ReadPresentedPixels().Select(x => (byte)Math.Round(x * 255)), Is.EqualTo(dithered));

        var audio = renderer.Audio;
        var history = audio.History;
        history[0][0] = .25f;
        audio.InvalidateHistory();
        renderer.SetGraphicsOptions(renderer.Options with { Exposure = 1 });
        Assert.That(renderer.Audio, Is.SameAs(audio));
        Assert.That(audio.History, Is.SameAs(history));
        Assert.That(history[0][0], Is.EqualTo(.25f));
        Span<AudioHistory.HistoryRange> updates = new AudioHistory.HistoryRange[2];
        Assert.That(audio.GetHistoryUpdates(updates), Is.GreaterThan(0), "Changing exposure must not consume pending audio uploads.");
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original), "An option edit cannot change the previously rendered frame.");
        passes.Clear();
        renderer.Render(false);
        Assert.That(passes, Is.EqualTo(new[] { "Audio upload", "Scene", "Output" }), "No extra normal-render pass is needed.");
        byte[] brighter = renderer.ReadPixels(false);
        double[] light = [.001, .125, .5];
        for (int c = 0; c < 3; c++)
        {
            double mapped = 1 - Math.Exp(-2 * (float)(Half)light[c]);
            Assert.That(brighter[c], Is.EqualTo(255 * Srgb(mapped)).Within(1));
            Assert.That(brighter[c], Is.GreaterThan(original[c]));
        }
        Assert.That(brighter.Skip(32 * 9 * 4), Is.EqualTo(original.Skip(32 * 9 * 4)), "Display colors bypass exposure.");
        Assert.That(brighter.Where((_, i) => i % 4 == 3), Is.All.EqualTo(255));
        renderer.SetGraphicsOptions(renderer.Options with { Exposure = 0 });
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
        renderer.Resize(17, 11);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Length, Is.EqualTo(17 * 11 * 4));
        Assert.That(renderer.ReloadShaders(), Is.True, renderer.ShaderError);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false)[0], Is.EqualTo(original[0]));
    }

    [TestCase("MandelLightv4", "ShowDepth"), TestCase("MandelLightv4", "ShowShadows"),
     TestCase("MandelLightv5", "ShowDepth"), Category("GPU")]
    public void InlineDiagnosticsIgnoreGlobalExposure(string scene, string view)
    {
        using var shaders = new SceneShaders(scene);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: new RenderClock(), initialScene: scene);
        var p = renderer.ActiveShader!.Parameters!;
        p.SetBoolean(p.Items.Single(x => x.Name == "View").Children.Single(x => x.Name == view), true);
        renderer.Render(false);
        byte[] original = renderer.ReadPixels(false);
        foreach (double exposure in new[] { -12.0, 12.0 })
        {
            renderer.SetGraphicsOptions(renderer.Options with { Exposure = exposure });
            renderer.Render(false);
            Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
        }
    }

    [Test, Category("GPU")]
    public void AllVisualizationScenesCompileWithoutExposureParameters()
    {
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, Width, Height, Bands, 60, shaderDirectory: ShaderDirectory);
        foreach (var scene in renderer.Scenes)
        {
            renderer.SelectScene(scene.Name);
            Assert.That(scene.Parameters == null || !ShaderParameters.Leaves(scene.Parameters.Items).Any(x => x.Name == "Exposure"),
                Is.True, scene.Name);
        }
    }

    [TestCase(false, false, 1, 1, .5f), TestCase(false, false, 1, 1, 2),
     TestCase(true, false, 1, 1, 2), TestCase(true, false, 6, 2.5f, 2),
     TestCase(true, false, 6, 2.5f, 4096), TestCase(true, false, 1, 1, 1f / 4096),
     TestCase(false, true, 1, 1, 4096), TestCase(true, true, 6, 2.5f, 4096), Category("GPU")]
    public void PresentationShaderMapsSyntheticColorsOnAnOffscreenFp16Surface(bool scRgb, bool displayColor,
        float headroom, float whiteScale, float gain)
    {
        // WARP tests the real shader and constant-buffer layout without relying on an HDR monitor.
        global::Vortice.Direct3D11.D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.None,
            [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        using (device)
        using (context)
        {
            string common = ShaderSource.Read(ShaderDirectory, "Common/Inputs.hlsl");
            using var vertex = device.CreateVertexShader(Compiler.Compile(common, "VS", "Inputs.hlsl", "vs_5_0").Span);
            using var pixel = device.CreatePixelShader(Compiler.Compile(common + ShaderSource.Read(ShaderDirectory, "Common/Present.hlsl"),
                "PS", "Present.hlsl", "ps_5_0").Span);
            var description = new Texture2DDescription
            {
                Width = 1, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float,
                SampleDescription = new(1, 0), BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            using var input = device.CreateTexture2D(description);
            using var inputTarget = device.CreateRenderTargetView(input);
            using var inputView = device.CreateShaderResourceView(input);
            using var output = device.CreateTexture2D(description);
            using var target = device.CreateRenderTargetView(output);
            description.BindFlags = BindFlags.None;
            description.Usage = ResourceUsage.Staging;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            using var staging = device.CreateTexture2D(description);
            using var buffer = device.CreateBuffer(32, BindFlags.ConstantBuffer);
            using var rasterizer = device.CreateRasterizerState(new RasterizerDescription
                { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = true });
            context.RSSetState(rasterizer);
            context.RSSetViewport(0, 0, 1, 1);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.VSSetShader(vertex);
            context.PSSetShader(pixel);
            context.PSSetConstantBuffer(3, buffer);
            context.UpdateSubresource(new PostprocessingConstants
                { Mapping = new(scRgb ? 1 : 0, whiteScale, gain, headroom) }, buffer);
            foreach (var color in new[] { new Vector3(0, .0001f, .003f), new Vector3(.25f, 1, 8), new Vector3(.02f, .041f, .75f) })
            {
                context.ClearRenderTargetView(inputTarget, new Color4(color.X, color.Y, color.Z, displayColor ? 1 : 0));
                context.OMSetRenderTargets(target);
                context.PSSetShaderResource(9, inputView);
                context.Draw(3, 0);
                context.PSSetShaderResource(9, null!);
                context.CopyResource(staging, output);
                var map = context.Map(staging, 0, MapMode.Read, global::Vortice.Direct3D11.MapFlags.None);
                var bytes = new byte[8];
                try { Marshal.Copy(map.DataPointer, bytes, 0, bytes.Length); }
                finally { context.Unmap(staging, 0); }
                var actual = MemoryMarshal.Cast<byte, Half>(bytes);
                for (int c = 0; c < 3; c++)
                {
                    double value = (float)(Half)color[c];
                    double expected;
                    if (displayColor)
                    {
                        value = Math.Clamp(value, 0, 1);
                        expected = scRgb ? (value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4)) * whiteScale : value;
                    }
                    else
                    {
                        double mapped = headroom * (1 - Math.Exp(-value * gain / headroom));
                        expected = scRgb ? mapped * whiteScale : Srgb(mapped);
                    }
                    Assert.That((float)actual[c], Is.EqualTo(expected).Within(Math.Max(.000002, Math.Abs(expected) * .002)),
                        $"color {color}, channel {c}");
                }
                Assert.That((float)actual[3], Is.EqualTo(1));
            }
            context.ClearState();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PostprocessingConstants { public Vector4 Mapping, Dither; }
    static double Srgb(double value) => value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
}

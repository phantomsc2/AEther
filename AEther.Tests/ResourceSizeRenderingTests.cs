using System;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [Test, Category("GPU")]
    public void LightSizesGrowShrinkAndPreserveUnrelatedAllocations()
    {
        using var shaders = new SceneShaders("MandelLight");
        string file = Path.Combine(shaders.Path, "Scenes", "MandelLight.hlsl");
        File.WriteAllText(file, File.ReadAllText(file).Replace("uint2(513,1025)", "uint2(17,33)"));
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 80, 48, 8, 60, shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: shaders.Scene);
        PointSet(renderer, "Sizes.Geometry.y", 0u);
        PointFeed(renderer, 0); renderer.Render(false);
        var raster = PointField<RasterLight>(renderer, "rasterLight");
        var lookup = PointField<object>(raster, "lookup");
        var surface = PointField<object>(raster, "surface");
        Assert.That((raster.AllocatedSizes.SurfaceWidth, raster.AllocatedSizes.SurfaceHeight), Is.EqualTo((80, 48)));
        foreach (uint count in new uint[] { 0, 1, 2, 3, 7, 180, 700, 7 })
        {
            PointSet(renderer, "Sizes.PointLights", count);
            renderer.Render(false);
            var records = PointRecords(renderer);
            Assert.That(records.Length, Is.EqualTo(count));
            Assert.That(records.All(p => float.IsFinite(p.Position.X) && Math.Abs(p.Position.Length() - 5.7) < .001), Is.True);
            Assert.That(PointField<object>(raster, "lookup"), Is.SameAs(lookup));
            Assert.That(PointField<object>(raster, "surface"), Is.SameAs(surface));
            using var buffer = raster.LightsView.Resource;
            using var typed = buffer.QueryInterface<global::Vortice.Direct3D11.ID3D11Buffer>();
            Assert.That(typed.Description.ByteWidth, Is.EqualTo(24 * Math.Max(1, count)));
        }
        var lastLights = raster.LightsView;
        // Bypass the contract validator to exercise a real D3D creation failure
        // after allocating the candidate light buffer, without exhausting memory.
        Assert.Catch(() => raster.Configure(raster.AllocatedSizes with { PointLights = 8, LookupWidth = 16385 }));
        Assert.That(raster.LightsView, Is.SameAs(lastLights));
        Assert.That(raster.LightCount, Is.EqualTo(7));
        Assert.That(PointField<object>(raster, "lookup"), Is.SameAs(lookup));
        PointSet(renderer, "Sizes.PointLights", uint.MaxValue);
        Assert.DoesNotThrow(() => renderer.Render(false));
        Assert.That(renderer.ActiveShader!.Parameters!.GetUnsigned(PointFind(renderer, "Sizes.PointLights")), Is.EqualTo(7));
        Assert.That(renderer.ShaderError, Does.Contain("PointLights"));
        PointSet(renderer, "Lights.Radius", .2f); renderer.Render(false);
        Assert.That(renderer.ShaderError, Is.Null);
        Assert.That(PointField<object>(raster, "lookup"), Is.SameAs(lookup));
        renderer.Resize(96, 54); renderer.Render(false);
        Assert.That((raster.AllocatedSizes.SurfaceWidth, raster.AllocatedSizes.SurfaceHeight), Is.EqualTo((96, 54)));
        Assert.That(PointField<object>(raster, "surface"), Is.Not.SameAs(surface));
        Assert.That(PointField<object>(raster, "lookup"), Is.SameAs(lookup));
    }

    [Test, Category("GPU")]
    public void ExplicitTextureSizesSurviveCanvasResizeAndReplaceOnlyTheirGroup()
    {
        using var shaders = new SceneShaders("MandelLight");
        string file = Path.Combine(shaders.Path, "Scenes", "MandelLight.hlsl");
        File.WriteAllText(file, File.ReadAllText(file)
            .Replace("uint2 RasterAirlightLut;", "uint2 RasterAirlightLut; uint2 RasterSurface; uint2 RasterAirlight;")
            .Replace("uint2(513,1025)", "uint2(17,33), uint2(40,24), uint2(20,12)"));
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 80, 48, 8, 60, shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: shaders.Scene);
        PointFeed(renderer, 0); renderer.Render(false);
        var raster = PointField<RasterLight>(renderer, "rasterLight");
        var surface = PointField<object>(raster, "surface");
        var air = PointField<object>(raster, "air");
        var lights = raster.LightsView;
        var lookup = PointField<object>(raster, "lookup");
        renderer.Resize(96, 54); renderer.Render(false);
        Assert.That(PointField<object>(raster, "surface"), Is.SameAs(surface));
        Assert.That(PointField<object>(raster, "air"), Is.SameAs(air));
        PointSet(renderer, "Sizes.RasterAirlight.x", 24u); renderer.Render(false);
        Assert.That(PointField<object>(raster, "air"), Is.Not.SameAs(air));
        Assert.That(PointField<object>(raster, "surface"), Is.SameAs(surface));
        Assert.That(raster.LightsView, Is.SameAs(lights));
        PointSet(renderer, "Sizes.RasterAirlightLut.x", 19u); renderer.Render(false);
        Assert.That(PointField<object>(raster, "lookup"), Is.Not.SameAs(lookup));
        Assert.That(raster.LightsView, Is.SameAs(lights));
        Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Any(v => v > 0), Is.True);
    }

    [Test, Category("GPU")]
    public void FluidSizesRecreateOnlySimulationAndRejectInvalidEdits()
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 128, 72, 8, 60, shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: shaders.Scene);
        renderer.Render(false);
        Assert.That((renderer.Fluid.Width, renderer.Fluid.Height), Is.EqualTo((512, 512)));
        var original = renderer.Fluid;
        PointSet(renderer, "Sizes.FluidVelocity.x", 256u);
        PointSet(renderer, "Sizes.FluidVelocity.y", 128u);
        renderer.Render(false);
        Assert.That((renderer.Fluid.Width, renderer.Fluid.Height), Is.EqualTo((256, 128)));
        Assert.That(renderer.Fluid, Is.Not.SameAs(original));
        var resized = renderer.Fluid;
        PointSet(renderer, "Emitter.Rate", .2f); renderer.Render(false);
        Assert.That(renderer.Fluid, Is.SameAs(resized));
        renderer.Resize(256, 144); renderer.Render(false);
        Assert.That(renderer.Fluid, Is.SameAs(resized), "Canvas size changes with the same physical aspect preserve explicit fluid storage.");
        PointSet(renderer, "Sizes.FluidVelocity.y", 127u); renderer.Render(false);
        Assert.That(renderer.Fluid, Is.SameAs(resized));
        Assert.That(renderer.ActiveShader!.Parameters!.GetUnsigned(PointFind(renderer, "Sizes.FluidVelocity.y")), Is.EqualTo(128));
        var projection = renderer.Fluid.CheckProjection(renderer.FluidPrograms);
        Assert.That(projection.After, Is.LessThan(projection.Before));
    }
}

using System;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    static RasterOptions RasterSettings(Renderer r) => ((RasterEffect)r.ActiveShader!.Effects[0]).Options;
    static FluidOptions FluidSettings(Renderer r) => ((FluidEffect)r.ActiveShader!.Effects[0]).Options;
    static void SetRasterOptions(Renderer r, RasterOptions options) => ((RasterEffect)r.ActiveShader!.Effects[0]).Options = options;
    static void SetFluidOptions(Renderer r, FluidOptions options) => ((FluidEffect)r.ActiveShader!.Effects[0]).Options = options;

    static SceneDefinition FluidDefinition(params global::Vortice.DXGI.Format[] formats) => new("MandelFluid",
        (d,c,s) => [new FluidEffect("Fluid",d,c,s,"MandelFluid.hlsl",new FluidOptions(128,128),formats)]);

    [Test, Category("GPU")]
    public void NamedInstancesKeepParametersAndConstantBuffersIndependent()
    {
        using var shaders = new SceneShaders("Spectrum");
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"), """
            cbuffer Parameters { float Gain = .5; };
            float4 PS(PSin input):SV_Target { return DisplayOutput(Gain.xxx); }
            """);
        var setup = new SceneDefinition("Spectrum", (d,c,s) =>
            [new FullscreenEffect("First",d,c,s,"Spectrum.hlsl"), new FullscreenEffect("Second",d,c,s,"Spectrum.hlsl")],
            (effects,frame) => { effects[0].Draw(frame); effects[1].Draw(frame); });
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle,64,36,8,60,shaderDirectory:shaders.Path,
            initialScene:"Spectrum",sceneDefinitions:[setup]);
        var effects = renderer.ActiveShader!.Effects;
        var first = effects[0].Parameters!;
        var second = effects[1].Parameters!;
        first.SetFloat(first.Items.Single(), .2f);
        second.SetFloat(second.Items.Single(), .8f);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false)[0], Is.EqualTo(204).Within(1), "The second pass must upload its own values.");
        Assert.That(first.GetFloat(first.Items.Single()), Is.EqualTo(.2f));
        Assert.That(PointField<object>(effects[0].Display,"parameterBuffer"),
            Is.Not.SameAs(PointField<object>(effects[1].Display,"parameterBuffer")));
        Assert.That(renderer.ReloadShaders(), Is.True);
        effects = renderer.ActiveShader!.Effects;
        Assert.That(effects[0].Parameters!.GetFloat(effects[0].Parameters!.Items.Single()), Is.EqualTo(.2f));
        Assert.That(effects[1].Parameters!.GetFloat(effects[1].Parameters!.Items.Single()), Is.EqualTo(.8f));
    }

    [Test, Category("GPU")]
    public void DeactivationReleasesFluidStorageAndSettingsSurviveRendererRestart()
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle,64,36,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        var effect = (FluidEffect)renderer.ActiveShader!.Effects[0];
        effect.Options = new FluidOptions(128,64);
        PointSet(renderer,"Emitter.Rate",.3f);
        renderer.Render(false);
        var original = effect.Simulation;
        renderer.SelectScene(null);
        Assert.That(effect.Simulation, Is.Null);
        renderer.SelectScene(shaders.Scene); renderer.Render(false);
        Assert.That(effect.Simulation, Is.Not.SameAs(original));
        Assert.That(effect.Options, Is.EqualTo(new FluidOptions(128,64)));
        Assert.That(renderer.ReloadShaders(), Is.True);
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation, Is.Null);
        var saved = renderer.GetSceneSettings(shaders.Scene)!;
        using var nextWindow = new HiddenWindow();
        using var next = new Renderer(nextWindow.Handle,64,36,8,60,shaderDirectory:shaders.Path);
        next.RestoreSceneSettings(shaders.Scene,saved); next.SelectScene(shaders.Scene); next.Render(false);
        var restored = (FluidEffect)next.ActiveShader!.Effects[0];
        Assert.That(restored.Options, Is.EqualTo(new FluidOptions(128,64)));
        Assert.That(restored.Parameters!.GetFloat(restored.Parameters.Items.Single(p=>p.Name=="Emitter").Children.Single(p=>p.Name=="Rate")), Is.EqualTo(.3f));
    }

    [Test, Category("GPU")]
    public void LightSizesGrowShrinkAndPreserveUnrelatedAllocations()
    {
        using var shaders = new SceneShaders("MandelLight");
        string file = Path.Combine(shaders.Path, "Scenes", "MandelLight.hlsl");
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 80, 48, 8, 60, shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: shaders.Scene);
        SetRasterOptions(renderer, RasterSettings(renderer) with { Instances = 0u, LookupWidth = 17, LookupHeight = 33 });
        PointFeed(renderer, 0); renderer.Render(false);
        var raster = ((RasterEffect)renderer.ActiveShader!.Effects[0]).Raster!;
        var lookup = PointField<object>(raster, "lookup");
        var surface = PointField<object>(raster, "surface");
        Assert.That((raster.AllocatedSizes.SurfaceWidth, raster.AllocatedSizes.SurfaceHeight), Is.EqualTo((80, 48)));
        foreach (uint count in new uint[] { 0, 1, 2, 3, 7, 180, 700, 7 })
        {
            SetRasterOptions(renderer, RasterSettings(renderer) with { PointLights = count });
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
        SetRasterOptions(renderer, RasterSettings(renderer) with { PointLights = uint.MaxValue });
        Assert.DoesNotThrow(() => renderer.Render(false));
        Assert.That(RasterSettings(renderer).PointLights, Is.EqualTo(7));
        Assert.That(renderer.ShaderError, Does.Contain("PointLights"));
        renderer.Render(false);
        Assert.That(renderer.ShaderError, Does.Contain("PointLights"), "Rejected settings remain visible until another edit.");
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
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 80, 48, 8, 60, shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: shaders.Scene);
        SetRasterOptions(renderer, RasterSettings(renderer) with { LookupWidth=17, LookupHeight=33, SurfaceWidth=40, SurfaceHeight=24, AirWidth=20, AirHeight=12 });
        PointFeed(renderer, 0); renderer.Render(false);
        var raster = ((RasterEffect)renderer.ActiveShader!.Effects[0]).Raster!;
        var surface = PointField<object>(raster, "surface");
        var air = PointField<object>(raster, "air");
        var lights = raster.LightsView;
        var lookup = PointField<object>(raster, "lookup");
        renderer.Resize(96, 54); renderer.Render(false);
        Assert.That(PointField<object>(raster, "surface"), Is.SameAs(surface));
        Assert.That(PointField<object>(raster, "air"), Is.SameAs(air));
        SetRasterOptions(renderer, RasterSettings(renderer) with { AirWidth = (int)24u }); renderer.Render(false);
        Assert.That(PointField<object>(raster, "air"), Is.Not.SameAs(air));
        Assert.That(PointField<object>(raster, "surface"), Is.SameAs(surface));
        Assert.That(raster.LightsView, Is.SameAs(lights));
        SetRasterOptions(renderer, RasterSettings(renderer) with { LookupWidth = (int)19u }); renderer.Render(false);
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
        Assert.That((((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.Width, ((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.Height), Is.EqualTo((512, 512)));
        var original = ((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!;
        SetFluidOptions(renderer, FluidSettings(renderer) with { Width = (int)256u });
        SetFluidOptions(renderer, FluidSettings(renderer) with { Height = (int)128u });
        renderer.Render(false);
        Assert.That((((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.Width, ((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.Height), Is.EqualTo((256, 128)));
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!, Is.Not.SameAs(original));
        var resized = ((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!;
        PointSet(renderer, "Emitter.Rate", .2f); renderer.Render(false);
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!, Is.SameAs(resized));
        renderer.Resize(256, 144); renderer.Render(false);
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!, Is.SameAs(resized), "Canvas size changes with the same physical aspect preserve explicit fluid storage.");
        SetFluidOptions(renderer, FluidSettings(renderer) with { Height = (int)127u }); renderer.Render(false);
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!, Is.SameAs(resized));
        Assert.That(FluidSettings(renderer).Height, Is.EqualTo(128));
        var projection = ((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.CheckProjection(((FluidEffect)renderer.ActiveShader!.Effects[0]).Programs);
        Assert.That(projection.After, Is.LessThan(projection.Before));
    }
}

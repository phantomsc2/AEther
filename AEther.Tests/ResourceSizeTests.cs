using System;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;
using Vortice.D3DCompiler;
using Vortice.Direct3D11.Shader;

namespace AEther.Tests;

public class ResourceSizeTests
{
    static ShaderParameters Reflect(string name, float value)
    {
        var code = ShaderSource.Compile($"cbuffer Parameters {{ float {name} = {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}; }}; float4 PS():SV_Target {{ return {name}; }}", "PS", "Parameters.hlsl", "ps_5_0");
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(code.Span);
        return new(reflection.GetConstantBufferByName("Parameters"));
    }

    [Test]
    public void StageLayoutsMergeLocallyAndSnapshotsStayIndependent()
    {
        var first = Reflect("Gain", .5f);
        var snapshot = first.Snapshot();
        first.SetFloat(first.Items.Single(), .8f);
        Assert.That(snapshot.GetFloat(snapshot.Items.Single()), Is.EqualTo(.5));
        var merged = ShaderParameters.Merge([first, Reflect("Other", .2f)])!;
        Assert.That(merged.Items.Select(p => p.Name), Is.EqualTo(new[] { "Gain", "Other" }));
        Assert.Throws<InvalidOperationException>(() => ShaderParameters.Merge([first, Reflect("Gain", .9f)]));
    }

    [Test]
    public void RasterDefaultsResolveCanvasAxesAndValidateWorkloads()
    {
        var defaults = new RasterOptions();
        var resolved = defaults.Resolve(1280,720);
        Assert.That((resolved.SurfaceWidth,resolved.SurfaceHeight,resolved.LookupWidth,resolved.LookupHeight), Is.EqualTo((1280,720,1280,720)));
        Assert.That(defaults.SurfaceWidth, Is.Zero);
        Assert.That(resolved.PointLights, Is.EqualTo(180));
        Assert.That((resolved.Vertices,resolved.Instances), Is.EqualTo((36,128)));
        Assert.Throws<InvalidOperationException>(() => (defaults with { PointLights=uint.MaxValue }).Resolve(1280,720));
        Assert.Throws<InvalidOperationException>(() => (defaults with { Vertices=uint.MaxValue, Instances=2 }).Resolve(1280,720));
        Assert.Throws<InvalidOperationException>(() => (defaults with { LookupWidth=1 }).Resolve(1280,720));
        Assert.Throws<InvalidOperationException>(() => (defaults with { AirWidth=-1 }).Resolve(1280,720));
    }

    [TestCase(256,128,true), TestCase(512,512,true), TestCase(17,128,false), TestCase(4096,128,false), TestCase(0,128,false)]
    public void FluidAxesAreValidatedIndependently(int width,int height,bool valid)
    {
        var options = new FluidOptions(width,height);
        if (valid) Assert.DoesNotThrow(options.Validate);
        else Assert.Throws<InvalidOperationException>(options.Validate);
    }
}

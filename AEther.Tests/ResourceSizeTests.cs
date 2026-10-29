using System;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;
using Vortice.D3DCompiler;
using Vortice.Direct3D11.Shader;

namespace AEther.Tests;

public class ResourceSizeTests
{
    static ShaderParameters Reflect(string fields, string defaults, string expression = "Gain")
    {
        string source = $$"""
            struct ResourceSizes { {{fields}} };
            cbuffer Parameters { ResourceSizes Sizes = { {{defaults}} }; float Gain = .5; };
            float4 PS() : SV_Target { return {{expression}}; }
            """;
        var code = ShaderSource.Compile(source, "PS", "Sizes.hlsl", "ps_5_0");
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(code.Span);
        return new(reflection.GetConstantBufferByName("Parameters"));
    }

    [Test]
    public void UnusedSizeDefaultsAreReflectedAndSnapshotsStayIndependent()
    {
        var values = Reflect("uint PointLights; uint2 Geometry;", "180, uint2(36,128)");
        var count = values.Items.Single(p => p.Name == "Sizes").Children.Single(p => p.Name == "PointLights");
        Assert.That(count.IsSize && count.IsUnsigned, Is.True);
        Assert.That(values.GetUnsigned(count), Is.EqualTo(180));
        var snapshot = values.Snapshot();
        values.SetUnsigned(count, 700);
        Assert.That(snapshot.GetUnsigned(count), Is.EqualTo(180));
        var sizes = SceneSizes.Resolve(values, true, false, 1280, 720);
        Assert.That(sizes.PointLights, Is.EqualTo(700));
        Assert.That((sizes.SurfaceWidth, sizes.SurfaceHeight), Is.EqualTo((1280, 720)));
        Assert.That((sizes.LookupWidth, sizes.LookupHeight), Is.EqualTo((1280, 720)));
        Assert.That((sizes.Vertices, sizes.Instances), Is.EqualTo((36, 128)));
        values.Reset();
        Assert.That(values.GetUnsigned(count), Is.EqualTo(180));
        values.Restore(snapshot);
        Assert.That(values.GetUnsigned(count), Is.EqualTo(180));
    }

    [Test]
    public void StageLayoutsMergeByPathAndRejectConflictingDefaults()
    {
        var first = Reflect("uint PointLights;", "180");
        var second = Reflect("uint2 Geometry;", "uint2(36,128)");
        var merged = ShaderParameters.Merge([first, second]);
        Assert.That(SceneSizes.Resolve(merged, true, false, 1280, 720).Instances, Is.EqualTo(128));
        Assert.Throws<InvalidOperationException>(() => ShaderParameters.Merge([first, Reflect("uint PointLights;", "96")]));
    }

    [TestCase("float PointLights; uint2 Geometry;", ".5, uint2(36,128)")]
    [TestCase("uint Typo;", "1")]
    [TestCase("uint2 PointLights; uint2 Geometry;", "uint2(1,1), uint2(36,128)")]
    [TestCase("uint PointLights;", "1")]
    [TestCase("uint PointLights; uint2 Geometry; uint2 RasterAirlightLut;", "1, uint2(36,128), uint2(1,4)")]
    public void InvalidSizeContractsAreRejected(string fields, string defaults)
    {
        var values = Reflect(fields, defaults);
        Assert.Throws<InvalidOperationException>(() => SceneSizes.Resolve(values, true, false, 1280, 720));
    }

    [TestCase(256, 128, true)]
    [TestCase(512, 512, true)]
    [TestCase(17, 128, false)]
    [TestCase(4096, 128, false)]
    [TestCase(0, 128, false)]
    public void FluidAxesAreValidatedIndependently(int width, int height, bool valid)
    {
        var values = Reflect("uint2 FluidVelocity;", $"uint2({width},{height})");
        if (valid)
        {
            var sizes = SceneSizes.Resolve(values, false, true, 1280, 720);
            Assert.That((sizes.FluidWidth, sizes.FluidHeight), Is.EqualTo((width, height)));
        }
        else Assert.Throws<InvalidOperationException>(() => SceneSizes.Resolve(values, false, true, 1280, 720));
    }
}

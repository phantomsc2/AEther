using System;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;
public partial class ShaderRenderingTests
{
    [Test,Category("GPU")]
    public void AtmosphereCenteredPresetMatchesReferenceLook()
    {
        using var shaders=new SceneShaders("Atmosphere",referenceAtmosphere:false);
        string file=Path.Combine(shaders.Path,"Scenes","Atmosphere.hlsl");
        string source=File.ReadAllText(file);
        File.WriteAllText(file,source[..source.IndexOf("float4 PS(PSin",StringComparison.Ordinal)]+"""
            float4 PS(PSin input):SV_TARGET
            {
                return float4(AtmosphereBeta(),AtmosphereScaleHeight());
            }
            """);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        renderer.Render(false);var values=RingReadScene(renderer);
        double[] expected=[.1*.049*16*.059,.1*.135*16*.059,.1*.490*16*.059,16];
        for(int c=0;c<4;c++) Assert.That(values[c],Is.EqualTo(expected[c]).Within(expected[c]*.001));
        foreach(string path in new[]{"Atmosphere.Density","Atmosphere.ScaleHeight","Atmosphere.RayleighScattering.x",
            "Atmosphere.RayleighScattering.y","Atmosphere.RayleighScattering.z","Phase.Anisotropy","Phase.Strength"})
            Assert.That(renderer.ActiveShader!.Effects[0].Parameters!.GetFloat(PointFind(renderer,path)),Is.EqualTo(.5f),path);
    }

    [TestCase("Atmosphere"),TestCase("Planet"),Category("GPU")]
    public void AtmosphereSunIsScatteringOnlyAt720p(string scene)
    {
        using var shaders=new SceneShaders(scene,multipleScattering:true,referenceAtmosphere:false);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:scene);
        var paths=ShaderParameters.Leaves(renderer.ActiveShader!.Effects[0].Parameters!.Items).Select(p=>p.Path).ToArray();
        Assert.That(paths,Does.Not.Contain("Transport.BackgroundEmission"));
        Assert.That(paths.Any(p=>p.EndsWith(".AngularRadius")),Is.False);
        renderer.Render(false);var image=RingReadScene(renderer);
        Assert.That(image.All(float.IsFinite),Is.True);
        Assert.That(image.Where((_,i)=>i%4!=3).Max(),Is.GreaterThan(.01));
        string directory=Path.GetFullPath(Path.Combine(ShaderDirectory,"..","..","artifacts","scattering-suns"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory,scene+"-default-1280x720.rgba"),renderer.ReadPixels(false));
        string phase=scene=="Planet"?"Atmosphere.Phase.":"Phase.";
        PointSet(renderer,phase+"Anisotropy",1);renderer.Render(false);
        Assert.That(RingReadScene(renderer).All(v=>float.IsFinite(v) && v>=0),Is.True,"Maximum anisotropy remains finite");
        PointSet(renderer,"Transport.L1",false);PointSet(renderer,"Transport.L2Plus",false);renderer.Render(false);
        Assert.That(RingReadScene(renderer).Where((_,i)=>i%4!=3),Is.All.Zero,"No direct disk remains");
        PointSet(renderer,"Transport.L1",true);PointSet(renderer,"Atmosphere.Density",0);renderer.Render(false);
        Assert.That(RingReadScene(renderer).Where((_,i)=>i%4!=3),Is.All.Zero,"No sun in vacuum");
    }
}
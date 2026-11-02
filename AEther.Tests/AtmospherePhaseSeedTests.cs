using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;
public partial class ShaderRenderingTests
{
    [TestCase("Planet"), TestCase("Atmosphere"), Category("GPU")]
    public void MultipleScatteringPhaseToggleUpdatesOnlyDependentCaches(string scene)
    {
        using var shaders=new SceneShaders(scene,multipleScattering:true);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:scene,timeProvider:new FroxelClock());
        string phase=scene=="Planet"?"Atmosphere.Phase.":"Phase.";
        string debug=scene=="Planet"?"Debug.MultipleScattering":"ShowMultipleScattering";
        var markers=new List<string>();
        renderer.TransportPassMarker=(s,b)=> { if(b && s.StartsWith("Atmosphere ")) markers.Add(s); };
        void Draw() { markers.Clear();renderer.Render(false);Assert.That(renderer.ShaderError,Is.Null); }
        void Rebuilt(bool multiple,bool sky,bool aerial) {
            Assert.That(markers.Contains("Atmosphere multiple scattering"),Is.EqualTo(multiple));
            Assert.That(markers.Contains("Atmosphere sky view"),Is.EqualTo(sky));
            Assert.That(markers.Contains("Atmosphere aerial perspective"),Is.EqualTo(aerial && scene=="Planet"));
            Assert.That(markers,Does.Not.Contain("Atmosphere transmittance"));
        }
        Draw();
        var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        Assert.That(effect.Parameters!.GetBoolean(PointFind(renderer,phase+"UseForMultipleScattering")),Is.False);
        var trans=AtmosphereRead(renderer,effect.TransmittanceTexture);
        var single=AtmosphereRead(renderer,effect.SkySunTexture);
        var baseline=AtmosphereRead(renderer,effect.MultipleScatteringTexture);
        var image=renderer.ReadPixels(false);
        var aerial=scene=="Planet"?ReadFroxelVolume(renderer,effect.AerialSunTexture):null;
        PointSet(renderer,phase+"UseForMultipleScattering",true);Draw();Rebuilt(true,true,true);
        Assert.That(AtmosphereRead(renderer,effect.MultipleScatteringTexture),Is.Not.EqualTo(baseline));
        Assert.That(AtmosphereRead(renderer,effect.SkySunTexture),Is.EqualTo(single));
        Assert.That(AtmosphereRead(renderer,effect.TransmittanceTexture),Is.EqualTo(trans));
        if(aerial!=null) Assert.That(ReadFroxelVolume(renderer,effect.AerialSunTexture),Is.EqualTo(aerial));
        Draw();Rebuilt(false,false,false);
        PointSet(renderer,phase+"Anisotropy",.2f);Draw();Rebuilt(true,true,true);
        PointSet(renderer,phase+"Strength",.7f);Draw();Rebuilt(true,true,true);
        PointSet(renderer,phase+"UseForMultipleScattering",false);Draw();Rebuilt(true,true,true);
        Assert.That(AtmosphereRead(renderer,effect.MultipleScatteringTexture),Is.EqualTo(baseline));
        // Restore only the display phase controls; disabled seed edits remain display-only.
        var values=renderer.ActiveShader!.Effects[0].Parameters!;
        PointSet(renderer,phase+"Anisotropy",values.GetDefaultFloat(PointFind(renderer,phase+"Anisotropy")));
        PointSet(renderer,phase+"Strength",values.GetDefaultFloat(PointFind(renderer,phase+"Strength")));
        Draw();Rebuilt(false,false,false);
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(image));

        PointSet(renderer,"Transport.L2Plus",false);Draw();
        PointSet(renderer,phase+"UseForMultipleScattering",true);Draw();Rebuilt(false,false,false);
        PointSet(renderer,phase+"Anisotropy",.3f);Draw();Rebuilt(false,false,false);
        PointSet(renderer,debug,true);Draw();Rebuilt(true,false,false);
        PointSet(renderer,phase+"Strength",.4f);Draw();Rebuilt(true,false,false);
        PointSet(renderer,debug,false);Draw();
        PointSet(renderer,"Transport.L2Plus",true);Draw();Rebuilt(false,true,true);
        PointSet(renderer,"Transport.L2Plus",false);Draw();
        PointSet(renderer,phase+"Anisotropy",.4f);Draw();Rebuilt(false,false,false);
        PointSet(renderer,"Transport.L2Plus",true);Draw();Rebuilt(true,true,true);
        foreach(float value in new[]{0f,1f}) {
            PointSet(renderer,phase+"Anisotropy",value);PointSet(renderer,phase+"Strength",value);Draw();
            Assert.That(AtmosphereRead(renderer,effect.MultipleScatteringTexture).All(v=>float.IsFinite(v) && v>=0),Is.True);
            Assert.That(RingReadScene(renderer).All(v=>float.IsFinite(v) && v>=0),Is.True);
        }
        Assert.That(renderer.ReloadActiveScene(),Is.True,renderer.ShaderError);
        Draw();
        Assert.That(markers,Does.Contain("Atmosphere multiple scattering"));
        Assert.That(markers,Does.Contain("Atmosphere sky view"));
        if(scene=="Planet") Assert.That(markers,Does.Contain("Atmosphere aerial perspective"));
    }

    [TestCase("Planet"), TestCase("Atmosphere"), Category("GPU")]
    public void DisabledMultiplePhaseSeedMatchesLegacyShader(string scene)
    {
        using var current=new SceneShaders(scene,multipleScattering:true);
        using var legacy=new SceneShaders(scene,multipleScattering:true);
        string file=Path.Combine(legacy.Path,"Common","Atmosphere.hlsl");
        File.WriteAllText(file,File.ReadAllText(file).Replace(
            "a+=ATMOSPHERE_PHASE.UseForMultipleScattering ? source*seedPhase : source/(4*AtmospherePi);",
            "a+=source/(4*AtmospherePi);"));
        using var aWindow=new HiddenWindow();using var bWindow=new HiddenWindow();
        using var a=new Renderer(aWindow.Handle,1280,720,8,60,shaderDirectory:current.Path,initialScene:scene,timeProvider:new FroxelClock());
        using var b=new Renderer(bWindow.Handle,1280,720,8,60,shaderDirectory:legacy.Path,initialScene:scene,timeProvider:new FroxelClock());
        a.Render(false);b.Render(false);
        Assert.That(a.ShaderError,Is.Null);Assert.That(b.ShaderError,Is.Null);
        Assert.That(a.ReadPixels(false),Is.EqualTo(b.ReadPixels(false)));
        var actual=AtmosphereRead(a,((AtmosphereEffect)a.ActiveShader!.Effects[0]).MultipleScatteringTexture);
        var expected=AtmosphereRead(b,((AtmosphereEffect)b.ActiveShader!.Effects[0]).MultipleScatteringTexture);
        for(int i=0;i<actual.Length;i++)
            Assert.That(actual[i],Is.EqualTo(expected[i]).Within(Math.Max(1e-12,Math.Abs(expected[i])*2e-6)),"Legacy LUT roundoff");
    }
}

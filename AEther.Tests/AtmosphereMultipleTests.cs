using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;
public partial class ShaderRenderingTests
{
    [TestCase("Atmosphere"),TestCase("Planet"),Category("GPU")]
    public void AtmosphereScatteringOrdersAreIndependent(string scene)
    {
        using var shaders=new SceneShaders(scene,multipleScattering:true);
        using var window=new HiddenWindow();var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:scene);
        var markers=new List<string>();renderer.TransportPassMarker=(s,b)=> {if(b) markers.Add(s);};
        void Draw(){markers.Clear();renderer.Render(false);}
        Draw();var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        Assert.That(markers,Does.Contain("Atmosphere multiple scattering"));
        var both=AtmosphereReadSky(renderer,effect);
        var trans=AtmosphereRead(renderer,effect.TransmittanceTexture);
        var lut=AtmosphereRead(renderer,effect.MultipleScatteringTexture);
        Assert.That(lut.All(v=>float.IsFinite(v) && v>=0),Is.True);
        Assert.That(lut.Where((_,i)=>i%4!=3).Max(),Is.GreaterThan(.00001));
        var aerialTrans=scene=="Planet"?ReadFroxelVolume(renderer,effect.AerialTransmissionTexture):null;
        PointSet(renderer,"Transport.L2Plus",false);Draw();var single=AtmosphereReadSky(renderer,effect);
        Assert.That(markers,Does.Not.Contain("Atmosphere multiple scattering"));
        PointSet(renderer,"Transport.L1",false);PointSet(renderer,"Transport.L2Plus",true);Draw();
        var multiple=AtmosphereReadSky(renderer,effect);
        Assert.That(multiple.Where((_,i)=>i%4!=3).Max(),Is.GreaterThan(.001),"L2+ remains visible without displayed L1");
        for(int i=0;i<both.Length;i++) if(i%4!=3) Assert.That(both[i],Is.EqualTo(single[i]+multiple[i]).Within(Math.Max(.00002,both[i]*.00002)),"Order additivity");
        Assert.That(AtmosphereRead(renderer,effect.TransmittanceTexture),Is.EqualTo(trans));
        if(aerialTrans!=null) Assert.That(ReadFroxelVolume(renderer,effect.AerialTransmissionTexture),Is.EqualTo(aerialTrans));
        var image=renderer.ReadPixels(false);PointSet(renderer,"Transport.L0",true);Draw();
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(image),"Reserved L0 does nothing");
        Assert.That(markers,Does.Not.Contain("Atmosphere sky view"));
        PointSet(renderer,"Sun.Azimuth",.1f);Draw();Assert.That(markers,Does.Not.Contain("Atmosphere multiple scattering"));
        PointSet(renderer,"Transport.L2Plus",false);Draw();
        Assert.That(AtmosphereReadSky(renderer,effect).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointSet(renderer,"Atmosphere.Density",.3f);Draw();Assert.That(markers,Does.Not.Contain("Atmosphere multiple scattering"));
        PointSet(renderer,"Transport.L2Plus",true);Draw();Assert.That(markers,Does.Contain("Atmosphere multiple scattering"));
        PointSet(renderer,"Atmosphere.Density",0);Draw();
        Assert.That(AtmosphereRead(renderer,effect.MultipleScatteringTexture).Where((_,i)=>i%4!=3),Is.All.Zero);
        Assert.That(AtmosphereReadSky(renderer,effect).Where((_,i)=>i%4!=3),Is.All.Zero);
        foreach(float value in new[]{.001f,1f}) {
            PointSet(renderer,"Atmosphere.Density",value);Draw();
            Assert.That(AtmosphereRead(renderer,effect.MultipleScatteringTexture).All(v=>float.IsFinite(v) && v>=0),Is.True);
        }
        Assert.That(renderer.ReloadActiveScene(),Is.True,renderer.ShaderError);Draw();Assert.That(markers,Does.Contain("Atmosphere multiple scattering"));
    }

    [TestCase(false),TestCase(true),Category("GPU")]
    public void AtmosphereMultipleLutMatchesIndependentClosure(bool phaseSeed)
    {
        using var shaders=new SceneShaders("Atmosphere",multipleScattering:true);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        PointSet(renderer,"Phase.UseForMultipleScattering",phaseSeed);
        if(phaseSeed) {
            PointSet(renderer,"Phase.Anisotropy",AtmosphereAnisotropySlider(.5));
            PointSet(renderer,"Phase.Strength",AtmosphereStrengthSlider(.25));
        }
        renderer.Render(false);var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        var lut=AtmosphereRead(renderer,effect.MultipleScatteringTexture);
        double[] beta=[.0058,.0135,.0331];
        foreach(var (x,y) in new[]{(31,0),(24,15),(12,8)}) {
            double h=AtmosphereReferenceAltitude(y/31.0,8),r=AtmosphereR+h,sunMu=2*x/31.0-1;
            double[] seed=new double[3],transfer=new double[3];
            for(int j=0;j<64;j++) {
                double mu=1-2*((j/8)+.5)/8,phi=2*Math.PI*((j%8)+.5)/8;
                double cosine=mu*sunMu+Math.Sqrt((1-mu*mu)*(1-sunMu*sunMu))*Math.Cos(phi);
                double phase=phaseSeed?AtmosphereReferencePhase(cosine,.5,.25):1/(4*Math.PI);
                double distance=AtmosphereReferenceDistance(r,mu);
                if(mu<0 && r*r*(1-mu*mu)<AtmosphereR*AtmosphereR) distance=-r*mu-Math.Sqrt(AtmosphereR*AtmosphereR-r*r*(1-mu*mu));
                const int n=256;double ds=distance/n,column=0;
                for(int k=0;k<n;k++) {
                    double d=(k+.5)*ds,radius=Math.Sqrt(r*r+d*(2*r*mu+d)),density=Math.Exp(-(radius-AtmosphereR)/8);
                    double sm=(r*sunMu+d*cosine)/radius;
                    bool shadow=sm<0 && radius*radius*(1-sm*sm)<AtmosphereR*AtmosphereR;
                    if(!shadow) {
                        double sunColumn=AtmosphereReferenceColumn(radius,sm,AtmosphereReferenceDistance(radius,sm));
                        for(int c=0;c<3;c++) seed[c]+=beta[c]*density*ds*Math.Exp(-beta[c]*(column+.5*density*ds+sunColumn))*phase/64;
                    }
                    column+=density*ds;
                }
                for(int c=0;c<3;c++) transfer[c]+=(1-Math.Exp(-beta[c]*column))/64;
            }
            for(int c=0;c<3;c++) {
                double expected=seed[c]/Math.Max(1-transfer[c],.0001);
                Assert.That(lut[4*(y*32+x)+c],Is.EqualTo(expected).Within(Math.Max(.00008,expected*.06)),"Independent dense-path closure");
            }
        }
    }
    [TestCase("Atmosphere"),TestCase("Planet"),Category("GPU"),Category("Performance")]
    public void AtmosphereMultipleTimingsAndCapturesAt720p(string scene)
    {
        using var shaders=new SceneShaders(scene,multipleScattering:true);
        using var window=new HiddenWindow();var clock=new RenderClock();var profile=new PipelineProfile();
        var history=profile.Track("gpu.atmosphere_multiple_scattering_ms");
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,timeProvider:clock,profile:profile,initialScene:scene);
        void Tick(){clock.Advance();renderer.Render(false);renderer.ReadPixels(false);renderer.PollGpuTimings();}
        for(int i=0;i<48;i++) { PointSet(renderer,"Atmosphere.Density",(scene=="Planet"?.5f:.0625f)+(i%2==0?0:.001f));Tick(); }
        long cursor=0;var samples=new List<TimingSample>();history.ReadSince(ref cursor,samples);
        var timings=samples.Skip(8).Select(s=>s.Milliseconds).Order().ToArray();Assert.That(timings.Length,Is.GreaterThan(24));
        string directory=Path.GetFullPath(Path.Combine(ShaderDirectory,"..","..","artifacts","multiple-scattering"));Directory.CreateDirectory(directory);
        string report=$"{scene}, 1280x720, {renderer.AdapterDescription}: multiple LUT mean {timings.Average():F4} ms, p95 {timings[(int)(.95*(timings.Length-1))]:F4} ms";
        File.WriteAllText(Path.Combine(directory,scene+"-timings.txt"),report);TestContext.Out.WriteLine(report);
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();Tick();
        File.WriteAllBytes(Path.Combine(directory,scene+"-both-1280x720.rgba"),renderer.ReadPixels());
        PointSet(renderer,"Transport.L2Plus",false);Tick();File.WriteAllBytes(Path.Combine(directory,scene+"-single-1280x720.rgba"),renderer.ReadPixels());
        PointSet(renderer,"Transport.L1",false);PointSet(renderer,"Transport.L2Plus",true);Tick();File.WriteAllBytes(Path.Combine(directory,scene+"-multiple-1280x720.rgba"),renderer.ReadPixels());
        if(scene=="Planet") {
            PlanetReferenceSettings(renderer);PointSet(renderer,"Sun.Azimuth",.5f);PointSet(renderer,"Sun.Altitude",.5f);Tick();var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
            var indirect=AtmosphereReadSky(renderer,effect);
            PointSet(renderer,"Transport.L1",true);PointSet(renderer,"Transport.L2Plus",false);Tick();
            var direct=AtmosphereReadSky(renderer,effect);
            Assert.That(direct.Take(3),Is.All.Zero,"Disk-center ray is in planet shadow");
            Assert.That(indirect.Take(3).Max(),Is.GreaterThan(.00001),"Indirect light reaches the direct-sun shadow");
        }
    }}

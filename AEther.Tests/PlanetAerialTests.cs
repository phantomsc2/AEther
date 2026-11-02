using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;
public partial class ShaderRenderingTests
{
    [Test,Category("GPU")]
    public void PlanetAerialCacheAndCutoutsAt720p()
    {
        using var shaders=new SceneShaders("Planet");
        using var window=new HiddenWindow();
        var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:shaders.Scene);
        var markers=new List<string>();
        renderer.TransportPassMarker=(name,start)=> { if(start) markers.Add(name); };
        void Draw() { markers.Clear(); renderer.Render(false); }
        Draw();
        var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        Assert.That(effect.AerialSunTexture.Description.Width,Is.EqualTo(32));
        Assert.That(markers,Does.Contain("Atmosphere aerial perspective"));
        Assert.That(markers,Does.Contain("Planet meteors"));
        var a=AtmosphereReadAerial(renderer,effect);
        var t=ReadFroxelVolume(renderer,effect.AerialTransmissionTexture);
        Assert.That(a.All(v=>float.IsFinite(v) && v>=0),Is.True);
        Assert.That(t.All(v=>float.IsFinite(v) && v>=0 && v<=1),Is.True);
        for(int p=0;p<32*32;p++) for(int c=0;c<3;c++) {
            Assert.That(a[p*4+c],Is.Zero);
            Assert.That(t[p*4+c],Is.EqualTo(1));
            for(int z=1;z<32;z++) Assert.That(t[4*(z*1024+p)+c],Is.LessThanOrEqualTo(t[4*((z-1)*1024+p)+c]));
        }
        var first=renderer.ReadPixels(false);
        Draw(); Assert.That(markers,Does.Not.Contain("Atmosphere aerial perspective"));
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(first));
        clock.Frame=600;Draw();
        Assert.That(renderer.ReadPixels(false).SequenceEqual(first),Is.False,"Orbiting cutouts move");
        Assert.That(markers,Does.Not.Contain("Atmosphere aerial perspective"));
        PointSet(renderer,"Shell.Speed",0);Draw();first=renderer.ReadPixels(false);
        clock.Frame=1200;Draw();Assert.That(renderer.ReadPixels(false),Is.EqualTo(first));
        PointSet(renderer,"Shell.Alt",.6f);Draw();Assert.That(markers,Does.Not.Contain("Atmosphere aerial perspective"));
        PointSet(renderer,"Camera.Yaw",.55f);Draw();Assert.That(markers,Does.Contain("Atmosphere aerial perspective"));
        PointSet(renderer,"Camera.Fov",.7f);Draw();
        Assert.That(markers,Does.Contain("Atmosphere aerial perspective"));Assert.That(markers,Does.Not.Contain("Atmosphere sky view"));
        PointSet(renderer,"Atmosphere.Phase.Strength",.6f);Draw();
        Assert.That(markers,Does.Not.Contain("Atmosphere sky view"));Assert.That(markers,Does.Not.Contain("Atmosphere aerial perspective"));
        Assert.That(markers,Does.Not.Contain("Atmosphere transmittance"));
        PointSet(renderer,"Shell.Count",0);Draw();Assert.That(markers,Does.Not.Contain("Atmosphere aerial perspective"));
        PointSet(renderer,"Debug.Aerial",true);Draw();Assert.That(markers,Does.Not.Contain("Atmosphere aerial perspective"));
        PointSet(renderer,"Debug.Transmission",true);Draw();Assert.That(RingReadScene(renderer).All(float.IsFinite),Is.True);
        PointSet(renderer,"Debug.Aerial",false);
        renderer.Resize(1281,720);Draw();Assert.That(markers,Does.Contain("Atmosphere aerial perspective"));
        PointSet(renderer,"Atmosphere.Density",0);Draw();
        a=AtmosphereReadAerial(renderer,effect);t=ReadFroxelVolume(renderer,effect.AerialTransmissionTexture);
        Assert.That(a.Where((_,i)=>i%4!=3),Is.All.Zero);Assert.That(t,Is.All.EqualTo(1));
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        Assert.That(renderer.ReloadActiveScene(),Is.True,renderer.ShaderError);Draw();
        Assert.That(markers,Does.Contain("Atmosphere aerial perspective"));
    }
    [Test,Category("GPU"),Category("Performance")]
    public void PlanetAerialQualityAndTimingsAt720p()
    {
        using var shaders=new SceneShaders("Planet");
        using var window=new HiddenWindow();
        var clock=new RenderClock();var profile=new PipelineProfile();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,timeProvider:clock,profile:profile,initialScene:shaders.Scene);
        var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        string directory=Path.GetFullPath(Path.Combine(ShaderDirectory,"..","..","artifacts","planet-aerial"));Directory.CreateDirectory(directory);
        void Tick() { clock.Advance();renderer.Render(false);renderer.ReadPixels(false);renderer.PollGpuTimings(); }
        var report=new List<string>{"1280x720, "+renderer.AdapterDescription};
        var stages=new[]{"atmosphere_aerial_perspective","planet_meteors","scene"};
        var histories=stages.ToDictionary(stage=>stage,stage=>profile.Track("gpu."+stage+"_ms"));
        var cursors=stages.ToDictionary(stage=>stage,stage=>0L);
        foreach(int size in new[]{32,64}) {
            effect.AerialSize=size;
            for(int i=0;i<48;i++) { PointSet(renderer,"Camera.Yaw",i%2==0?.5f:.5001f);Tick(); }
            foreach(string stage in stages) {
                long cursor=cursors[stage];var samples=new List<TimingSample>();histories[stage].ReadSince(ref cursor,samples);cursors[stage]=cursor;
                var values=samples.Skip(8).Select(sample=>sample.Milliseconds).Order().ToArray();
                Assert.That(values.Length,Is.GreaterThan(24));
                report.Add($"{size} cubed {stage}: mean {values.Average():F4} ms, p95 {values[(int)(.95*(values.Length-1))]:F4} ms");
            }
        }
        // Freeze motion for an image comparison that isolates cache resolution.
        PointSet(renderer,"Shell.Speed",0);PointSet(renderer,"Camera.Yaw",.5f);
        effect.AerialSize=32;Tick();var coarse=RingReadScene(renderer);
        effect.AerialSize=64;Tick();var fine=RingReadScene(renderer);
        double error=0,energy=0;
        for(int i=0;i<coarse.Length;i++) if(i%4!=3) { error+=Math.Pow(coarse[i]-fine[i],2);energy+=fine[i]*fine[i]; }
        report.Add($"32 versus 64 cubed relative linear image RMS: {Math.Sqrt(error/Math.Max(energy,1e-20)):P4}");
        effect.AerialSize=32;PointSet(renderer,"Shell.Speed",.4f);
        for(int phase=0;phase<3;phase++) { for(int i=0;i<180;i++) clock.Advance();Tick();File.WriteAllBytes(Path.Combine(directory,$"orbit-{phase}-1280x720.rgba"),renderer.ReadPixels()); }
        // Independent finite-path reference: radial inward camera ray, clear noon light.
        PlanetReferenceSettings(renderer);PointSet(renderer,"Sun2.Intensity",0);Tick();
        var a=AtmosphereReadAerial(renderer,effect);
        var tr=ReadFroxelVolume(renderer,effect.AerialTransmissionTexture);
        double r=2.94*AtmosphereR*Math.Pow(3,.25),H=.55*AtmosphereR,top=AtmosphereR+10*H;
        double far=r+top;
        foreach(var sample in new[]{(15,15,12),(4,20,23),(25,8,31)}) {
            var (x,y,z)=sample;
            double dx=-(2*x/31.0-1)*(1280.0/720),dy=1-2*y/31.0,dz=-1/Math.Tan(Math.PI/3);
            double n=Math.Sqrt(dx*dx+dy*dy+dz*dz);dx/=n;dy/=n;dz/=n;
            double distance=far*Math.Pow(z/31.0,2)/(-dz);
            double impact=r*r*(1-dz*dz),b=r*dz;
            double begin=Math.Max(0,-b-Math.Sqrt(Math.Max(0,top*top-impact)));
            double end=Math.Min(distance,-b+Math.Sqrt(Math.Max(0,top*top-impact)));
            if(impact<AtmosphereR*AtmosphereR) end=Math.Min(end,-b-Math.Sqrt(AtmosphereR*AtmosphereR-impact));
            double[] expected=new double[3],beta=[.000087,.0002025,.0004965];double column=0;
            const int steps=1024;double ds=Math.Max(0,end-begin)/steps;
            for(int j=0;j<steps;j++) {
                double d=begin+(j+.5)*ds,px=d*dx,py=d*dy,pz=r+d*dz;
                double radius=Math.Sqrt(px*px+py*py+pz*pz),density=Math.Exp(-(radius-AtmosphereR)/H);
                double mu=(py*.5+pz*Math.Sqrt(.75))/radius,c=dy*.5+dz*Math.Sqrt(.75);
                bool shadow=mu<0 && radius*radius*(1-mu*mu)<AtmosphereR*AtmosphereR;
                if(!shadow) {
                    double sunColumn=PlanetColumn(radius,mu,AtmosphereReferenceDistance(radius,mu,top));
                    for(int ch=0;ch<3;ch++) expected[ch]+=20*AtmosphereReferencePhase(c,.855,.25)*beta[ch]*density*ds*Math.Exp(-beta[ch]*(column+.5*density*ds+sunColumn));
                }
                column+=density*ds;
            }
            for(int ch=0;ch<3;ch++) {
                int index=4*((z*32+y)*32+x)+ch;
                Assert.That(a[index],Is.EqualTo(expected[ch]).Within(Math.Max(.002,expected[ch]*.04)),"Finite-path scattering");
                Assert.That(tr[index],Is.EqualTo(Math.Exp(-beta[ch]*column)).Within(.004),"Finite-path transmission");
            }
        }
        File.WriteAllLines(Path.Combine(directory,"quality-and-timings.txt"),report);
        foreach(string line in report) TestContext.Out.WriteLine(line);
    }    [Test,Category("GPU")]
    public void PlanetShellKeepsExactAltitudeInRandomOrbitalPlanes()
    {
        using var shaders=new SceneShaders("Planet");
        string path=Path.Combine(shaders.Path,"Scenes","Planet.hlsl");
        string source=File.ReadAllText(path);
        File.WriteAllText(path,source[..source.IndexOf("float4 PS(PSin",StringComparison.Ordinal)]+"float4 PS(PSin input):SV_TARGET { return float4(PlanetMeteorCenter((uint)input.Position.x)+3,1); }");
        using var window=new HiddenWindow();var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:shaders.Scene);
        var values=renderer.ActiveShader!.Effects[0].Parameters!;
        Assert.That(values.Items.Select(p=>p.Name),Is.EqualTo(new[]{"Atmosphere","Camera","Debug","Shell","Sun","Sun2","Transport"}));
        foreach(float altitude in new[]{0f,.03903904f,1f}) foreach(long frame in new[]{0L,1200L}) {
            PointSet(renderer,"Shell.Alt",altitude);clock.Frame=frame;renderer.Render(false);
            var pixels=RingReadScene(renderer);double radius=1+.002+1.998*altitude;
            double minY=10,maxY=-10,sumY=0;
            for(int k=0;k<1000;k++) {
                double x=pixels[k*4]-3,y=pixels[k*4+1]-3,z=pixels[k*4+2]-3;
                Assert.That(Math.Sqrt(x*x+y*y+z*z),Is.EqualTo(radius).Within(.006),"Centers remain on the exact spherical shell");
                minY=Math.Min(minY,y/radius);maxY=Math.Max(maxY,y/radius);sumY+=y/radius;
            }
            Assert.That(minY,Is.LessThan(-.95));Assert.That(maxY,Is.GreaterThan(.95));
            Assert.That(Math.Abs(sumY/1000),Is.LessThan(.08),"Distribution covers both hemispheres");
        }
    }}

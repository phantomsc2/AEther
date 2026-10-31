using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    static void PlanetReferenceSettings(Renderer renderer)
    {
        foreach (var (path, value) in new[] { ("Atmosphere.RayleighScattering.x", .058f), ("Atmosphere.RayleighScattering.y", .135f),
            ("Atmosphere.RayleighScattering.z", .331f), ("Atmosphere.ScaleHeight", (float)(.5+Math.Log2(.55/.1868)/6)),
            ("Atmosphere.Density", (float)Math.Pow(.15,1/2.53951953)),
            ("Orbit.Azimuth", 0f), ("Orbit.Elevation", .5f), ("Orbit.Altitude", .25f),
            ("Sun.Azimuth", 0f), ("Sun.Altitude", 2f/3), ("Sun.Intensity", .5f) }) PointSet(renderer, path, value);
    }

    static double PlanetDensity(double h, double scale = .55*AtmosphereR)
    {
        return Math.Exp(-h/scale);
    }
    static double PlanetColumn(double r,double mu,double distance,double scale = .55*AtmosphereR)
    {
        const int n=2048;double ds=distance/n,sum=0;
        for(int i=0;i<=n;i++)
        {
            double d=i*ds,h=Math.Sqrt(r*r+d*(2*r*mu+d))-AtmosphereR;
            sum+=(i==0 || i==n ? 1 : i%2==0 ? 2 : 4)*PlanetDensity(h,scale);
        }
        return sum*ds/3;
    }
    // Independent dense double-precision quadrature, with no LUT or sampling-helper reuse.
    static double[] PlanetReference(int x, int y, double scale = .55*AtmosphereR, double orbit = .25, double cutoff = 10, bool eclipse = false, int steps = 4096)
    {
        double top = AtmosphereR+cutoff*scale;
        double r = 2.94*AtmosphereR*Math.Pow(3,orbit);
        double diskAngle = Math.Asin(AtmosphereR/r);
        bool disk = y<64;
        double v = (disk ? y : y-64)/63.0;
        double core = Math.Asin(Math.Min(1,(AtmosphereR+2*scale)/r));
        double k = (core-diskAngle)/(Math.PI-core);
        double angle = disk ? diskAngle*(1-(1-v)*(1-v)) : diskAngle+(Math.PI-diskAngle)*k*v/(1-v+k);
        double phi = 2*Math.PI*x/255;
        double dx = -Math.Sin(angle)*Math.Cos(phi), dy = Math.Sin(angle)*Math.Sin(phi), dz = -Math.Cos(angle);
        double b = r*dz, impact2 = r*r*(1-dz*dz);
        if (impact2>top*top || -b+Math.Sqrt(Math.Max(0,top*top-impact2))<=0) return new double[3];
        double begin = Math.Max(0,-b-Math.Sqrt(Math.Max(0,top*top-impact2)));
        double end = disk ? -b-Math.Sqrt(Math.Max(0,AtmosphereR*AtmosphereR-impact2))
            : -b+Math.Sqrt(Math.Max(0,top*top-impact2));
        double ds = (end-begin)/steps, column = 0;
        double[] beta = [.000087,.0002025,.0004965], result = new double[3]; // .015x density, over thousands of km.
        // Rotate world suns into the reference frame (observer on +Z, right = -X).
        double az = eclipse ? .618*2*Math.PI : 0, el = eclipse ? (-89+178*.359)*Math.PI/180 : 0;
        var suns = (eclipse ? new[]{(.294*2*Math.PI,(.233-.5)*Math.PI,40*.484)}
            : new[]{(0.0,Math.PI/6,20.0),(Math.PI,Math.PI/9,10.0)})
            .Select(s =>
            {
                var (sa,se,intensity)=s;
                double wx=Math.Cos(se)*Math.Sin(sa), wy=Math.Sin(se), wz=Math.Cos(se)*Math.Cos(sa);
                return (sx:wx*Math.Cos(az)-wz*Math.Sin(az),
                    sy:-wx*Math.Sin(el)*Math.Sin(az)+wy*Math.Cos(el)-wz*Math.Sin(el)*Math.Cos(az),
                    sz:wx*Math.Cos(el)*Math.Sin(az)+wy*Math.Sin(el)+wz*Math.Cos(el)*Math.Cos(az),intensity);
            }).ToArray();
        double g=eclipse ? -.95+1.9*.580 : 0, a=eclipse ? 0 : 1;
        for (int i=0;i<steps;i++)
        {
            double t = begin+(i+.5)*ds, px = t*dx, py=t*dy, pz=r+t*dz;
            double radius=Math.Sqrt(px*px+py*py+pz*pz), density=PlanetDensity(radius-AtmosphereR,scale);
            foreach (var (sx,sy,sz,intensity) in suns)
            {
                double mu=(px*sx+py*sy+pz*sz)/radius;
                if (mu<0 && radius*radius*mu*mu>radius*radius-AtmosphereR*AtmosphereR) continue;
                double sunColumn=PlanetColumn(radius,mu,AtmosphereReferenceDistance(radius,mu,top),scale);
                double c=dx*sx+dy*sy+dz*sz;
                double phase=(1-g*g)*(1+a*c*c)/(4*Math.PI*(1+a*(1+2*g*g)/3)*Math.Pow(1+g*g-2*g*c,1.5));
                for (int channel=0;channel<3;channel++)
                    result[channel]+=intensity*phase*beta[channel]*density*ds*
                        Math.Exp(-beta[channel]*(column+.5*density*ds+sunColumn));
            }
            column+=density*ds;
        }
        return result;
    }

    [Test,Category("GPU")]
    public void PlanetCenteredControlsPreserveTheShadowAtmosphere()
    {
        using var shaders=new SceneShaders("Planet");
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        renderer.Render(false);
        var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        var transmittance=AtmosphereRead(renderer,effect.TransmittanceTexture);
        // The screenshot used H=(.1+1.4*.062)R, density=.172 and these spectral coefficients.
        double column=.1868*AtmosphereR*(1-Math.Exp(-10));
        double[] scattering=[.201,.454,.687];
        for(int c=0;c<3;c++)
            Assert.That(transmittance[c],Is.EqualTo(Math.Exp(-.01*.172*scattering[c]*column)).Within(.0002));
        foreach(float value in new[]{0f,1f})
        {
            PointSet(renderer,"Atmosphere.ScaleHeight",value);renderer.Render(false);
            Assert.That(RingReadScene(renderer).All(v=>float.IsFinite(v) && v>=0),Is.True,"Log height endpoints stay finite");
        }
        PointSet(renderer,"Atmosphere.Density",0);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero,"Density still reaches vacuum");
    }

    [Test,Category("GPU")]
    public void PlanetDiskAndLimbMatchIndependentTransport()
    {
        using var shaders=new SceneShaders("Planet");
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        PlanetReferenceSettings(renderer);
        renderer.Render(false);
        var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        Assert.That((effect.SkyViewTexture.Description.Width,effect.SkyViewTexture.Description.Height),Is.EqualTo((256u,128u)));
        var sky=AtmosphereRead(renderer,effect.SkyViewTexture);
        var transmittance=AtmosphereRead(renderer,effect.TransmittanceTexture);
        // At U=0 rays point straight outward: the exponential column has a closed form.
        double scale=.55*AtmosphereR,limit=10*scale;
        double[] beta=[.000087,.0002025,.0004965];
        foreach(int y in new[]{0,16,32,48,63})
        {
            double h=AtmosphereReferenceAltitude(y/63.0,scale);
            double column=scale*(Math.Exp(-h/scale)-Math.Exp(-limit/scale));
            for(int c=0;c<3;c++)
                Assert.That(transmittance[4*y*256+c],Is.EqualTo(Math.Exp(-beta[c]*column)).Within(.0002),$"Radial transmittance at row {y}, channel {c}");
        }
        Assert.That(sky.All(v=>float.IsFinite(v) && v>=0),Is.True);
        foreach (var (x,y) in new[] { (0,0),(64,40),(192,59),(0,66),(64,70),(128,90),(192,110) })
        {
            var expected=PlanetReference(x,y);
            for (int c=0;c<3;c++)
            {
                double actual=sky[4*(y*256+x)+c];
                TestContext.Out.WriteLine($"Planet ({x},{y}), {c}: {actual:G6}, reference {expected[c]:G6}");
                Assert.That(actual,Is.EqualTo(expected[c]).Within(Math.Max(.0005,expected[c]*.035)),$"({x},{y}), channel {c}");
            }
        }
        for (int y=0;y<128;y++)
        for (int c=0;c<3;c++)
            Assert.That(sky[4*y*256+c],Is.EqualTo(sky[4*(y*256+255)+c]).Within(.0001),"Orbital LUT seam");
        Assert.That(sky[4*(116*256+64)+2],Is.GreaterThan(.005),"Exponential glow beyond the old shell's angular extent");
        var pixels=renderer.ReadPixels(false);
        Assert.That(pixels.Skip(4*(360*1280+640)).Take(3).Max(),Is.GreaterThan(20),"Foreground atmospheric scattering");
        Assert.That(effect.Parameters!.Items.Any(p=>p.Name=="Surface"),Is.False,"No surface-lighting controls");
        Assert.That(ShaderParameters.Leaves(effect.Parameters.Items).Any(p=>p.Path=="Atmosphere.Height"),Is.False);
        PointSet(renderer,"Atmosphere.Density",0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero,"No reflected surface remains when scattering is disabled");
    }

    [Test,Category("GPU")]
    public void PlanetOrbitViewAndLifecycleRespectLutDependencies()
    {
        using var shaders=new SceneShaders("Planet");
        File.Copy(Path.Combine(ShaderDirectory,"Scenes","Atmosphere.hlsl"),Path.Combine(shaders.Path,"Scenes","Atmosphere.hlsl"));
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        var markers=new List<string>();
        renderer.TransportPassMarker=(s,b)=>{if(b && s.StartsWith("Atmosphere ")) markers.Add(s);};
        void Draw(params string[] expected)
        {
            markers.Clear();renderer.Render(false);Assert.That(markers,Is.EqualTo(expected));
            Assert.That(RingReadScene(renderer).All(float.IsFinite),Is.True);
        }
        Draw("Atmosphere transmittance","Atmosphere sky view");
        var original=renderer.ReadPixels(false);
        Draw();Assert.That(renderer.ReadPixels(false),Is.EqualTo(original));
        PointSet(renderer,"View.Azimuth",0);Draw();
        Assert.That(RingReadScene(renderer).Where((_,i)=>i%4!=3),Is.All.Zero,"Looking away from the thinner atmosphere sees space");
        PointSet(renderer,"View.Azimuth",.5f);Draw();
        foreach (string path in new[] { "Orbit.Azimuth","Orbit.Elevation","Orbit.Altitude","Phase.Anisotropy","Sun2.Intensity" })
        { PointSet(renderer,path,.7f);Draw("Atmosphere sky view"); }
        foreach (float value in new[] { 0f,1f })
        {
            PointSet(renderer,"Orbit.Altitude",value);PointSet(renderer,"Orbit.Elevation",value);
            Draw("Atmosphere sky view");
        }
        PointSet(renderer,"ShowSkyView",true);Draw();
        var raw=renderer.ReadPixels(false);
        renderer.SetGraphicsOptions(renderer.Options with { Exposure=8 });Draw();Assert.That(renderer.ReadPixels(false),Is.EqualTo(raw));
        renderer.Resize(1281,721);Draw();
        Assert.That(renderer.ReloadActiveScene(),Is.True,renderer.ShaderError);Draw("Atmosphere transmittance","Atmosphere sky view");
        Assert.That(renderer.ActiveShader!.Effects[0].Parameters!.GetFloat(PointFind(renderer,"Orbit.Altitude")),Is.EqualTo(1));
        renderer.SelectScene("Atmosphere");renderer.Render(false);
        Assert.That(renderer.ActiveShader!.Effects[0].Parameters!.Items.Any(p=>p.Name=="Orbit"),Is.False);
        renderer.SelectScene("Planet");Draw("Atmosphere transmittance","Atmosphere sky view");
        PointSet(renderer,"Atmosphere.Density",.25f);Draw("Atmosphere transmittance","Atmosphere sky view");
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();
        renderer.SetGraphicsOptions(renderer.Options with { Exposure=0 });renderer.Resize(1280,720);
        Draw("Atmosphere transmittance","Atmosphere sky view");Assert.That(renderer.ReadPixels(false),Is.EqualTo(original));
        PointSet(renderer,"Sun.Intensity",0);PointSet(renderer,"Sun2.Intensity",0);Draw("Atmosphere sky view");
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero,"No unlit emission");
    }

    [TestCase(.1,1),TestCase(1.49,0),TestCase(.55,.25),Category("GPU")]
    public void PlanetDensityFocusedSamplingMatchesLongerReference(double scaleR,double orbit)
    {
        using var shaders=new SceneShaders("Planet");
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        PlanetReferenceSettings(renderer);
        // Logarithmic height control centered at .1868R.
        PointSet(renderer,"Atmosphere.ScaleHeight",(float)(.5+Math.Log2(scaleR/.1868)/6));
        PointSet(renderer,"Orbit.Altitude",(float)orbit);
        renderer.Render(false);
        var sky=AtmosphereRead(renderer,((AtmosphereEffect)renderer.ActiveShader!.Effects[0]).SkyViewTexture);
        foreach(var (x,y) in new[]{(0,0),(64,59),(64,70),(128,90),(64,110),(0,127)})
        {
            var expected=PlanetReference(x,y,scaleR*AtmosphereR,orbit);
            var longer=PlanetReference(x,y,scaleR*AtmosphereR,orbit,20);
            for(int c=0;c<3;c++)
            {
                Assert.That(expected[c],Is.EqualTo(longer[c]).Within(Math.Max(.0005,longer[c]*.005)),"10H versus 20H tail convergence (0.5% or 0.0005 radiance)");
                double actual=sky[4*(y*256+x)+c];
                TestContext.Out.WriteLine($"H={scaleR}R orbit={orbit} ({x},{y}) channel {c}: {actual:G6}, reference {expected[c]:G6}");
                Assert.That(actual,Is.EqualTo(expected[c]).Within(Math.Max(.0005,expected[c]*.035)),"Nonuniform GPU quadrature versus dense reference");
            }
        }
    }

    [Test,Category("GPU")]
    public void PlanetEclipseMatchesIndependentTransport()
    {
        using var shaders=new SceneShaders("Planet");
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        PlanetReferenceSettings(renderer);
        foreach(var (path,value) in new[]{("Orbit.Altitude",0f),("Orbit.Azimuth",.618f),("Orbit.Elevation",.359f),
            ("Sun.Altitude",.233f),("Sun.Azimuth",.294f),("Sun.Intensity",.484f),("Sun2.Intensity",0f),
            ("Phase.Anisotropy",.580f),("Phase.Shape",0f)}) PointSet(renderer,path,value);
        renderer.Render(false);
        var sky=AtmosphereRead(renderer,((AtmosphereEffect)renderer.ActiveShader!.Effects[0]).SkyViewTexture);
        foreach(var (x,y) in new[]{(0,0),(64,59),(192,59),(0,70),(64,70),(128,70),(192,70),(64,90),(192,110)})
        {
            var expected=PlanetReference(x,y,orbit:0,eclipse:true);
            for(int c=0;c<3;c++)
                Assert.That(sky[4*(y*256+x)+c],Is.EqualTo(expected[c]).Within(Math.Max(.0005,expected[c]*.035)),
                    $"Eclipse ({x},{y}) channel {c}");
        }
    }

    [Test,Category("GPU"),Category("Performance")]
    public void PlanetTimingAndCapturesAt720p()
    {
        using var shaders=new SceneShaders("Planet");
        using var window=new HiddenWindow();
        var clock=new RenderClock();var profile=new PipelineProfile();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene,timeProvider:clock,profile:profile);
        void Tick(){clock.Advance();renderer.Render(false);renderer.ReadPixels(false);renderer.PollGpuTimings();}
        Tick();
        for(int i=0;i<16;i++){PointSet(renderer,"Orbit.Azimuth",i%2==0 ? .01f : .02f);Tick();}
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();Tick();
        for(int i=0;i<280;i++)Tick();
        string directory=Path.GetFullPath(Path.Combine(ShaderDirectory,"..","..","artifacts","planet"));Directory.CreateDirectory(directory);
        var report=new List<string>{$"1280x720, {renderer.AdapterDescription}"};
        foreach(string stage in new[]{"atmosphere_transmittance","atmosphere_sky_view","scene"})
        {
            var timing=profile.Recent("gpu."+stage+"_ms");Assert.That(timing,Is.Not.Null);
            report.Add($"{stage}: mean {timing!.Value.Mean:F4} ms, p95 {timing.Value.P95:F4} ms");
        }
        File.WriteAllLines(Path.Combine(directory,"timings.txt"),report);foreach(var line in report)TestContext.Out.WriteLine(line);
        void Capture(string name){Tick();string path=Path.Combine(directory,name+"-1280x720.rgba");File.WriteAllBytes(path,renderer.ReadPixels());TestContext.AddTestAttachment(path);}
        Capture("default");
        renderer.SetGraphicsOptions(renderer.Options with { Exposure=2.616 });Capture("shadow-preset");
        renderer.SetGraphicsOptions(renderer.Options with { Exposure=0 });
        PointSet(renderer,"ShowSkyView",true);Capture("sky-view");PointSet(renderer,"ShowSkyView",false);
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();PointSet(renderer,"Orbit.Azimuth",.25f);Capture("terminator");
        PointSet(renderer,"Orbit.Altitude",0);Capture("low-orbit");
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();
        PointSet(renderer,"Orbit.Altitude",0);PointSet(renderer,"Orbit.Azimuth",.618f);PointSet(renderer,"Orbit.Elevation",.359f);
        PointSet(renderer,"Sun.Altitude",.233f);PointSet(renderer,"Sun.Azimuth",.294f);PointSet(renderer,"Sun.Intensity",.484f);
        PointSet(renderer,"Sun2.Intensity",0);PointSet(renderer,"Phase.Anisotropy",.580f);PointSet(renderer,"Phase.Shape",0);
        PointSet(renderer,"View.Altitude",.458f);PointSet(renderer,"View.Azimuth",.469f);Capture("eclipse");
    }
}

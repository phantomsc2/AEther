using System;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [TestCase("Planet",false),TestCase("Planet",true),TestCase("Atmosphere",false),TestCase("Atmosphere",true),Category("GPU")]
    public void AtmosphereLinearSamplingMatchesMidpointReference(string scene,bool shadowed)
    {
        using var shaders=new SceneShaders(scene);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:scene);
        Assert.That(ShaderParameters.Leaves(renderer.ActiveShader!.Effects[0].Parameters!.Items).Any(p=>p.Path=="Atmosphere.NonUniformSampling"),Is.False);
        if(scene=="Planet") PlanetReferenceSettings(renderer);
        if(shadowed && scene=="Planet")
        {
            foreach(var (path,value) in new[]{("Orbit.Altitude",0f),("Orbit.Azimuth",.618f),("Orbit.Elevation",.359f),
                ("Sun.Altitude",.233f),("Sun.Azimuth",.294f),("Sun.Intensity",.484f),("Sun2.Intensity",0f),
                ("Phase.Anisotropy",.580f),("Phase.Shape",0f)}) PointSet(renderer,path,value);
        }
        else if(shadowed) {PointSet(renderer,"Sun.Altitude",.48f);PointSet(renderer,"Sun2.Intensity",0);}
        renderer.Render(false);
        var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        var trans=AtmosphereRead(renderer,effect.TransmittanceTexture);
        var sky=AtmosphereRead(renderer,effect.SkyViewTexture);
        Assert.That(trans.All(float.IsFinite) && sky.All(float.IsFinite),Is.True);
        double scale=scene=="Planet" ? .55*AtmosphereR : 8,top=AtmosphereR+10*scale;
        double limit=Math.Sqrt(top*top-AtmosphereR*AtmosphereR);
        double[] beta=scene=="Planet" ? [.000087,.0002025,.0004965] : [.0058,.0135,.0331];
        foreach(int y in new[]{0,32,63})
        foreach(int x in new[]{0,128,255})
        {
            double h=AtmosphereReferenceAltitude(y/63.0,scale),r=AtmosphereR+h,rho=Math.Sqrt(h*(2*AtmosphereR+h));
            double distance=top-r+x/255.0*(rho+limit-(top-r));
            double mu=distance<1e-6 ? 1 : Math.Clamp((limit*limit-rho*rho-distance*distance)/(2*r*distance),-1,1);
            double ds=distance/128,column=0;
            for(int i=0;i<128;i++)
            {
                double t=(i+.5)*ds,altitude=Math.Sqrt(r*r+t*(2*r*mu+t))-AtmosphereR;
                column+=Math.Exp(-altitude/scale)*ds;
            }
            for(int c=0;c<3;c++) Assert.That(trans[4*(y*256+x)+c],Is.EqualTo(Math.Exp(-beta[c]*column)).Within(.00005));
        }
        foreach(var (x,y) in scene=="Planet" ? new[]{(0,0),(192,59),(64,70),(128,90)} : new[]{(0,0),(64,100),(0,127)})
        {
            double[] expected;
            if(scene=="Planet") expected=PlanetReference(x,y,orbit:shadowed ? 0 : .25,eclipse:shadowed,steps:64);
            else
            {
                double az=2*Math.PI*x/255,el=.5*Math.PI*Math.Pow(1-y/127.0,2);
                expected=AtmosphereReferenceSky(az,el,shadowed ? -.02*Math.PI : Math.PI/6,steps:64);
                if(!shadowed)
                {
                    var second=AtmosphereReferenceSky(az,el,Math.PI/9,Math.PI,10,steps:64);
                    for(int c=0;c<3;c++) expected[c]+=second[c];
                }
            }
            // The reference uses midpoint attenuation; GPU segments integrate their
            // constant extinction exactly and interpolate the sunlight LUT.
            for(int c=0;c<3;c++) Assert.That(sky[4*(y*(int)effect.SkyViewTexture.Description.Width+x)+c],
                Is.EqualTo(expected[c]).Within(Math.Max(.0005,expected[c]*.035)),$"{scene} ({x},{y}) channel {c}");
        }
    }
}

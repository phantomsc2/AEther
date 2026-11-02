using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;
public partial class ShaderRenderingTests
{
    // Reconstruct cached nodes independently on the CPU; no combined GPU cache is retained.
    static float[] AtmosphereReadSky(Renderer renderer, AtmosphereEffect effect) => AtmosphereComposeReadback(renderer,
        AtmosphereRead(renderer,effect.SkySunTexture),
        AtmosphereRead(renderer,effect.SkyMultipleTexture),(int)effect.SkySunTexture.Description.Width,
        (int)effect.SkySunTexture.Description.Height,false);
    static float[] AtmosphereReadAerial(Renderer renderer, AtmosphereEffect effect) => AtmosphereComposeReadback(renderer,
        ReadFroxelVolume(renderer,effect.AerialSunTexture),
        ReadFroxelVolume(renderer,effect.AerialMultipleTexture),(int)effect.AerialSunTexture.Description.Width,
        (int)effect.AerialSunTexture.Description.Height,true);
    static float[] AtmosphereComposeReadback(Renderer renderer,float[] a,float[] m,int width,int height,bool aerial)
    {
        var values=renderer.ActiveShader!.Effects[0].Parameters!;
        var controls=ShaderParameters.Leaves(values.Items).ToDictionary(p=>p.Path,values.GetFloat);
        double V(string path)=>controls[path];
        bool planet=controls.ContainsKey("Camera.Alt");
        string phase=planet?"Atmosphere.Phase.":"Phase.";
        double g=1-Math.Pow(.05,2*V(phase+"Anisotropy")),strength=Math.Pow(V(phase+"Strength"),1.7958592832);
        var sun=AtmosphereTestDirection(2*Math.PI*V("Sun.Azimuth"),Math.PI*(V("Sun.Altitude")-.5));
        Vector3 forward=default,right=default,up=default;
        double limb=0,k=0,focal=0;
        if(planet)
        {
            double az=2*Math.PI*V("Camera.Long"),el=(-89+178*V("Camera.Lat"))*Math.PI/180;
            forward=-AtmosphereTestDirection(az,el);right=new((float)-Math.Cos(az),0,(float)Math.Sin(az));up=Vector3.Cross(forward,right);
            double r=Math.Pow(8.82,V("Camera.Alt")),h=.1868*Math.Pow(2,6*(V("Atmosphere.ScaleHeight")-.5));
            limb=Math.Asin(1/r);double core=Math.Asin(Math.Min(1,(1+2*h)/r));
            k=core-limb>1e-6?(core-limb)/(Math.PI-core):1;
            if(aerial)
            {
                double yaw=2*Math.PI*(V("Camera.Yaw")-.5),pitch=Math.PI*(V("Camera.Pitch")-.5);
                var rotated=(float)Math.Cos(yaw)*forward+(float)Math.Sin(yaw)*right;
                right=(float)Math.Cos(yaw)*right-(float)Math.Sin(yaw)*forward;
                forward=(float)Math.Cos(pitch)*rotated+(float)Math.Sin(pitch)*up;up=Vector3.Cross(forward,right);
                focal=1/Math.Tan((30+110*V("Camera.Fov"))*Math.PI/360);
            }
        }
        float aspect=PointField<int>(renderer,"width")/(float)PointField<int>(renderer,"height");
        var result=new float[a.Length];
        for(int y=0;y<height;y++) for(int x=0;x<width;x++)
        {
            Vector3 ray;
            if(aerial) ray=Vector3.Normalize((float)focal*forward+(float)((2.0*x/(width-1)-1)*aspect)*right+(float)(1-2.0*y/(height-1))*up);
            else if(!planet) ray=AtmosphereTestDirection(2*Math.PI*x/(width-1),.5*Math.PI*Math.Pow(1-y/(double)(height-1),2));
            else
            {
                bool disk=y<height/2;double v=(disk?y:y-height/2)/(double)(height/2-1);
                double angle=disk?limb*(1-(1-v)*(1-v)):limb+(Math.PI-limb)*k*v/(1-v+k);
                double phi=2*Math.PI*x/(width-1);
                ray=Vector3.Normalize((float)Math.Cos(angle)*forward+(float)Math.Sin(angle)*((float)Math.Cos(phi)*right+(float)Math.Sin(phi)*up));
            }
            double p=AtmosphereReferencePhase(Math.Clamp(Vector3.Dot(ray,sun),-1,1),g,strength);
            for(int z=0;z<a.Length/(4*width*height);z++)
            {
                int i=4*((z*height+y)*width+x);
                for(int c=0;c<3;c++) result[i+c]=(float)(a[i+c]*p+m[i+c]);
                result[i+3]=1;
            }
        }
        return result;
    }
    static Vector3 AtmosphereTestDirection(double az,double el)=>new((float)(Math.Cos(el)*Math.Sin(az)),(float)Math.Sin(el),(float)(Math.Cos(el)*Math.Cos(az)));

    [TestCase("Planet"),TestCase("Atmosphere"),Category("GPU")]
    public void AtmospherePhaseEditsReuseSeparatedTransport(string scene)
    {
        using var shaders=new SceneShaders(scene,multipleScattering:true);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:scene,timeProvider:new FroxelClock());
        var markers=new List<string>();renderer.TransportPassMarker=(s,b)=>{if(b && s.StartsWith("Atmosphere "))markers.Add(s);};
        renderer.Render(false);var effect=(AtmosphereEffect)renderer.ActiveShader!.Effects[0];
        var a=AtmosphereRead(renderer,effect.SkySunTexture);var m=AtmosphereRead(renderer,effect.SkyMultipleTexture);
        var aerial=scene=="Planet"?ReadFroxelVolume(renderer,effect.AerialSunTexture):null;
        var before=RingReadScene(renderer);markers.Clear();
        string prefix=scene=="Planet"?"Atmosphere.Phase.":"Phase.";
        PointSet(renderer,prefix+"Anisotropy",1);PointSet(renderer,prefix+"Strength",1);renderer.Render(false);
        Assert.That(markers,Is.Empty,"Phase is display-only");
        Assert.That(AtmosphereRead(renderer,effect.SkySunTexture),Is.EqualTo(a));
        Assert.That(AtmosphereRead(renderer,effect.SkyMultipleTexture),Is.EqualTo(m));
        if(aerial!=null)Assert.That(ReadFroxelVolume(renderer,effect.AerialSunTexture),Is.EqualTo(aerial));
        Assert.That(RingReadScene(renderer),Is.Not.EqualTo(before));
    }
    [Test,Category("GPU")]
    public void AtmosphereComposesSunAtPixelResolution()
    {
        using var shaders=new SceneShaders("Atmosphere",referenceAtmosphere:false);
        string file=System.IO.Path.Combine(shaders.Path,"Scenes","Atmosphere.hlsl");
        string source=System.IO.File.ReadAllText(file);
        int begin=source.IndexOf("[numthreads(8,8,1)]",StringComparison.Ordinal),end=source.IndexOf("float4 PS(PSin",begin,StringComparison.Ordinal);
        System.IO.File.WriteAllText(file,source[..begin]+"""
            [numthreads(8,8,1)]
            void BuildSkyView(uint3 id:SV_DispatchThreadID)
            {
                uint w,h; AtmosphereSkySunOutput.GetDimensions(w,h);
                if(id.x>=w || id.y>=h) return;
                AtmosphereTransport light;
                light.Sun=float3(1,.2,.1); light.Multiple=float3(.01,.02,.03);
                AtmosphereStoreSky(id.xy,light);
            }
            """+source[end..]);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        foreach(var (path,value) in new[]{("Sun.Azimuth",.13f),("View.Azimuth",.13f),("Sun.Altitude",.62f),("View.Altitude",.62f),
            ("Phase.Anisotropy",1f),("Phase.Strength",1f)})PointSet(renderer,path,value);
        renderer.Render(false);var image=RingReadScene(renderer);
        double az=2*Math.PI*.13f,el=Math.PI*(.62f-.5);
        var forward=AtmosphereTestDirection(az,el);var right=new Vector3((float)Math.Cos(az),0,(float)-Math.Sin(az));var up=Vector3.Cross(forward,right);
        double[] a=[1,.2,.1],m=[.01,.02,.03];
        foreach(int x in new[]{635,639,640,641,644,650})foreach(int y in new[]{358,360,362})
        {
            var ray=Vector3.Normalize((float)(1/Math.Tan(Math.PI/3))*forward+(float)((2*(x+.5)/1280-1)*1280/720)*right+(float)(1-2*(y+.5)/720)*up);
            double p=AtmosphereReferencePhase(Math.Clamp(Vector3.Dot(ray,forward),-1,1),.9975,1);
            for(int c=0;c<3;c++)Assert.That(image[4*(y*1280+x)+c],Is.EqualTo(a[c]*p+m[c]).Within((a[c]*p+m[c])*.025),"Exact per-pixel sun phase");
        }
    }

    [Test,Category("GPU")]
    public void PlanetPerPixelPhaseCaptureAt720p()
    {
        using var shaders=new SceneShaders("Planet",multipleScattering:true);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        foreach(var (path,value) in new[]{("Atmosphere.Density",.856f),("Atmosphere.Phase.Anisotropy",.682f),("Atmosphere.Phase.Strength",.905f),
            ("Atmosphere.RayleighScattering.x",.051f),("Atmosphere.RayleighScattering.y",.245f),("Atmosphere.RayleighScattering.z",.556f),
            ("Atmosphere.ScaleHeight",.132f),("Camera.Alt",.160f),("Camera.Pitch",.572f),("Sun.Altitude",.648f),("Sun.Azimuth",.677f),
            ("Sun.Intensity",.827f),("Shell.Count",1f),("Shell.Alt",.016f),("Shell.Size",.402f)})PointSet(renderer,path,value);
        renderer.Render(false);Assert.That(RingReadScene(renderer).All(float.IsFinite),Is.True);
        string directory=System.IO.Path.GetFullPath(System.IO.Path.Combine(ShaderDirectory,"..","..","artifacts","phase-split"));System.IO.Directory.CreateDirectory(directory);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory,"planet-per-pixel-1280x720.rgba"),renderer.ReadPixels(false));
    }

}

using System;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [Test, Category("GPU")]
    public void Froxel3EightDirectionPhaseHasCorrectSignAndFiniteEndpoints()
    {
        using var shaders=new SceneShaders("Froxel3");
        string path=Path.Combine(shaders.Path,"Scenes","Froxel3.hlsl");
        File.WriteAllText(path,File.ReadAllText(path).Replace(
            "float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }", """
            float4 PS(PSin input) : SV_Target {
                float g=2*Medium.Anisotropy-1;
                float3 p=float3(0,0,3), outDir=float3(0,0,1);
                return LinearOutput(float3(
                    UniformRingAngularAverage(p,outDir,4.5,.05,g,true),
                    UniformRingAngularAverage(p,-outDir,4.5,.05,g,true),
                    RingPhaseRelative(1,g)*1e-8));
            }
            """));
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1,1,8,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel3");
        foreach(float knob in new[] {0f,.2f,.35f,.5f,.65f,.8f,1f})
        {
            PointSet(renderer,"Medium.Anisotropy",knob);renderer.Render(false);
            var values=RingReadScene(renderer);Assert.That(values.All(float.IsFinite),Is.True);
            double g=Math.Clamp(2*(double)knob-1,-.9999,.9999), cosine=3/Math.Sqrt(4.5*4.5+9);
            for(int i=0;i<2;i++)
            {
                double c=i==0 ? cosine : -cosine;
                double expected=(1-g*g)/Math.Pow(1+g*g-2*g*c,1.5);
                Assert.That(values[i],Is.EqualTo(expected).Within(Math.Max(1e-6,expected*.003)));
            }
            if(knob==.5f) Assert.That(values[0],Is.EqualTo(1));
            if(knob==.65f) Assert.That(values[0],Is.GreaterThan(values[1]),"Positive g favors forward light propagation.");
            if(knob==1) Assert.That(values[2],Is.GreaterThan(1),"Endpoint retains a narrow finite peak.");
        }
    }
    [Test, Category("GPU")]
    public void Froxel3AnisotropyAffectsFogAndNotSurfaceLighting()
    {
        using var shaders=new SceneShaders("Froxel3");using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,128,72,8,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel3");
        var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];effect.CloudOptions=new(32,0);
        PointSet(renderer,"Medium.Anisotropy",.5f);
        renderer.Render(false);var isotropic=RingReadScene(renderer);
        PointSet(renderer,"Medium.Anisotropy",.8f);renderer.Render(false);
        Assert.That(RingReadScene(renderer).SequenceEqual(isotropic),Is.False);
        foreach(float knob in new[] {0f,1f})
        {
            PointSet(renderer,"Medium.Anisotropy",knob);renderer.Render(false);
            Assert.That(RingReadScene(renderer).All(float.IsFinite),Is.True);
        }
        effect.CloudOptions=new(32,1024);PointSet(renderer,"Medium.Density",0);
        PointSet(renderer,"Medium.Anisotropy",.5f);renderer.Render(false);var surface=RingReadScene(renderer);
        PointSet(renderer,"Medium.Anisotropy",1);renderer.Render(false);
        Assert.That(RingReadScene(renderer),Is.EqualTo(surface));
        PointSet(renderer,"Medium.Density",.36f);PointSet(renderer,"Medium.Anisotropy",.8f);
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);renderer.Render(false);
        Assert.That(RingReadScene(renderer).All(float.IsFinite),Is.True);
    }
    [Test, Category("GPU")]
    public void Froxel3LightingPeakDoesNotFollowDepthJitter()
    {
        using var shaders=new SceneShaders("Froxel3");
        string path=Path.Combine(shaders.Path,"Scenes","Froxel3.hlsl"),source=File.ReadAllText(path);
        int start=source.IndexOf("FroxelMedium EvaluateFroxelMedium("),end=source.IndexOf("float3 RingColor(");
        source=source.Substring(0,start)+"FroxelMedium EvaluateFroxelMedium(float3 world) { FroxelMedium m; m.Extinction=.015; m.Scattering=.014; m.Anisotropy=0; return m; }\n"+source.Substring(end);
        File.WriteAllText(path,source);
        using var window=new HiddenWindow();var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,128,72,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:"Froxel3");
        ((Froxel3Effect)renderer.ActiveShader!.Effects[0]).CloudOptions=new(32,0);
        PointSet(renderer,"Medium.Anisotropy",.5f);
        PointSet(renderer,"Ring.Speed",0);renderer.Render(false);var first=RingReadScene(renderer);
        clock.Frame=3;renderer.Render(false);Assert.That(RingReadScene(renderer),Is.EqualTo(first));
        File.WriteAllText(path,source.Replace("#define FROXEL_SEGMENT_SOURCE","// point-sampled reference"));
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);
        renderer.Render(false);var old=RingReadScene(renderer);
        clock.Frame=6;renderer.Render(false);
        Assert.That(RingReadScene(renderer).SequenceEqual(old),Is.False,"The former point-sampled light changes with depth jitter even in uniform fog.");
    }
    [Test, Category("GPU")]
    public void Froxel3SegmentPeakMatchesDenseRayIntegration()
    {
        var cases=new[] {
            (A:new System.Numerics.Vector3(4.5f,0,-.4f), B:new System.Numerics.Vector3(4.5f,0,.6f)),
            (A:new System.Numerics.Vector3(4.5f,-.5f,0), B:new System.Numerics.Vector3(4.5f,.5f,0)),
            (A:new System.Numerics.Vector3(4.2f,0,0), B:new System.Numerics.Vector3(4.8f,0,0)),
            (A:new System.Numerics.Vector3(0,0,1), B:new System.Numerics.Vector3(0,0,2)),
            (A:new System.Numerics.Vector3(1,2,3), B:new System.Numerics.Vector3(1.2f,2.1f,3.2f)),
        };
        using var shaders=new SceneShaders("Froxel3");
        string path=Path.Combine(shaders.Path,"Scenes","Froxel3.hlsl");
        string function="float4 PS(PSin input):SV_Target { float3 a=0,b=0; switch((uint)input.Position.x) {";
        for(int i=0;i<cases.Length;i++)
        {
            var c=cases[i];
            string V(System.Numerics.Vector3 v)=>$"float3({RingNumber(v.X)},{RingNumber(v.Y)},{RingNumber(v.Z)})";
            function+=$"case {i}: a={V(c.A)};b={V(c.B)};break;";
        }
        function+="}return LinearOutput(float3(UniformRingSegment(a,b,4.5,.05),UniformRingWeight(lerp(a,b,.23),4.5,.05),0));}";
        File.WriteAllText(path,File.ReadAllText(path).Replace("float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }",function));
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,cases.Length,1,8,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel3");
        renderer.Render(false);var values=RingReadScene(renderer);
        double oldError=0,newError=0;
        for(int i=0;i<cases.Length;i++)
        {
            double sum=0;
            for(int j=0;j<65536;j++)
            {
                var p=System.Numerics.Vector3.Lerp(cases[i].A,cases[i].B,(j+.5f)/65536);
                double rho=Math.Sqrt(p.X*p.X+p.Y*p.Y),axial=p.Z*p.Z+.05*.05;
                sum+=2*Math.PI*4.5/Math.Sqrt(((4.5-rho)*(4.5-rho)+axial)*((4.5+rho)*(4.5+rho)+axial));
            }
            double expected=sum/65536;
            TestContext.Out.WriteLine($"Segment {i}: reference {expected:F5}, integrated {values[4*i]:F5}, point {values[4*i+1]:F5}");
            Assert.That(values[4*i],Is.EqualTo(expected).Within(expected*.04));
            newError+=Math.Abs(values[4*i]-expected)/expected;
            oldError+=Math.Abs(values[4*i+1]-expected)/expected;
        }
        Assert.That(newError,Is.LessThan(oldError*.2));
    }
    [Test, Category("GPU")]
    public void Froxel3UniformIntegralAndOffsetCycles()
    {
        using var shaders=new SceneShaders("Froxel3");
        string path=Path.Combine(shaders.Path,"Scenes","Froxel3.hlsl");
        File.WriteAllText(path,File.ReadAllText(path).Replace(
            "float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }", """
            float4 PS(PSin input) : SV_Target {
                uint i=(uint)input.Position.x;
                float3 p=i/3==0 ? float3(4.5,0,.01) : i/3==1 ? float3(0,0,2) : float3(2,1,20);
                p=RingLocal(RingWorld(p,i%3),i%3);
                return LinearOutput(float3(UniformRingWeight(p,4.5,.05),RingCycle(i%3),0));
            }
            """));
        using var window=new HiddenWindow();var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,9,1,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:"Froxel3");
        renderer.Render(false);var values=RingReadScene(renderer);
        var points=new[] {new System.Numerics.Vector3(4.5f,0,.01f),new System.Numerics.Vector3(0,0,2),new System.Numerics.Vector3(2,1,20)};
        for(int i=0;i<9;i++)
        {
            double expected=RingQuadrature(points[i/3],4.5,.05,-Math.PI,2*Math.PI);
            Assert.That(values[4*i],Is.EqualTo(expected).Within(expected*.002));
        }
        Assert.That(values[5],Is.GreaterThan(values[1]));Assert.That(values[1],Is.GreaterThan(values[9]));
        clock.Frame=120;renderer.Render(false);Assert.That(RingReadScene(renderer)[1],Is.GreaterThan(.99));
        PointSet(renderer,"Ring.Speed",0);renderer.Render(false);var frozen=RingReadScene(renderer);
        clock.Frame=240;renderer.Render(false);Assert.That(RingReadScene(renderer),Is.EqualTo(frozen));
    }
    [Test, Category("GPU")]
    public void Froxel3SurfaceDirectionsMatchIndependentLambertQuadrature()
    {
        using var shaders=new SceneShaders("Froxel3");
        string path=Path.Combine(shaders.Path,"Scenes","Froxel3.hlsl");
        File.WriteAllText(path,File.ReadAllText(path).Replace(
            "float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }", """
            float4 PS(PSin input) : SV_Target {
                uint i=(uint)input.Position.x;
                float3 p=i<3 ? float3(0,0,0) : float3(2,1,2);
                float3 n=i%3==0 ? float3(1,0,0) : i%3==1 ? float3(0,0,1) : float3(0,0,-1);
                return LinearOutput(UniformRingDiffuse(p,n,4.5,.05)*.01);
            }
            """));
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,6,1,8,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel3");
        var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];effect.CloudOptions=new(8,0);
        var audio=Enumerable.Repeat(.01,64).ToArray();
        renderer.PostAudio(new(audio,32,DateTime.UnixEpoch) {AnalysisRow=System.Threading.Interlocked.Increment(ref pointRow)},2);
        renderer.Render(false);var values=RingReadScene(renderer);
        for(int pixel=0;pixel<6;pixel++)
        {
            double sum=0;
            for(int j=0;j<65536;j++)
            {
                double angle=2*Math.PI*(j+.5)/65536;
                double x=4.5*Math.Sin(angle)-(pixel<3 ? 0 : 2);
                double y=-4.5*Math.Cos(angle)-(pixel<3 ? 0 : 1);
                double z=pixel<3 ? 0 : -2;
                double r2=x*x+y*y+z*z;
                double cosine=Math.Max(0,(pixel%3==0 ? x : pixel%3==1 ? z : -z)/Math.Sqrt(r2));
                sum+=.01*4.5*cosine/(r2+.05*.05);
            }
            double expected=sum*(2*Math.PI/65536);
            for(int c=0;c<3;c++) Assert.That(values[4*pixel+c],Is.EqualTo(expected).Within(Math.Max(1e-6,expected*.06)),"Surface "+pixel);
        }
    }

    [Test, Category("GPU")]
    public void Froxel3CombinesFogWithMovingRasterAndRestoresSettings()
    {
        using var shaders=new SceneShaders("Froxel3");using var window=new HiddenWindow();var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:"Froxel3");
        var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];
        var markers=new System.Collections.Generic.List<string>();renderer.TransportPassMarker=(s,b)=> { if(b) markers.Add(s); };
        renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3).Any(v=>v>20),Is.True);
        PointFeed(renderer,0,.04,.02);PointSet(renderer,"Medium.Density",0);renderer.Render(false);
        var surface=renderer.ReadPixels(false);
        Assert.That(surface.Where((_,i)=>i%4==0).Count(v=>v>20),Is.GreaterThan(1000));
        PointFeed(renderer,2,.04,.02); renderer.Render(false); Assert.That(renderer.ReadPixels(false),Is.EqualTo(surface),"Audio independent");
        renderer.Render(false);Assert.That(renderer.ReadPixels(false),Is.EqualTo(surface));
        clock.Frame=60;renderer.Render(false);Assert.That(renderer.ReadPixels(false).SequenceEqual(surface),Is.False);
        effect.CloudOptions=new(32,0);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointFeed(renderer,0,.04,.02);PointSet(renderer,"Medium.Density",.159f);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4==0).Any(v=>v>20),Is.True);
        effect.CloudOptions=new(32,1024);renderer.Render(false);
        Assert.That(markers,Does.Not.Contain("Ring spectrum prefix"));Assert.That(markers,Does.Contain("Froxel raster"));
        Assert.That(markers,Does.Not.Contain("Froxel lights"));
        effect.CloudOptions=new(7,1024);renderer.Render(false);
        Assert.That(effect.CloudOptions.DepthSlices,Is.EqualTo(32),"Invalid configuration rolls back.");
        renderer.RestartAudio(new(new Domain(30,12,31),60));PointFeed(renderer,2,.03,.01);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4==2).Any(v=>v>10),Is.True);
        PointFeed(renderer,-1);renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3).Any(v=>v>20),Is.True);
        renderer.Resize(1281,721);renderer.Render(false);Assert.That(effect.Dimensions,Is.EqualTo((161,91,32)));
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];
        Assert.That(effect.CloudOptions,Is.EqualTo(new Froxel3Options(32,1024)));
        File.AppendAllText(Path.Combine(shaders.Path,"Scenes","Froxel3.hlsl"),"\nINVALID HLSL");
        Assert.That(renderer.ReloadShaders(),Is.False);Assert.DoesNotThrow(()=>renderer.Render(false));
        renderer.SelectScene(null);Assert.That(effect.Dimensions,Is.EqualTo((0,0,0)));
    }
}

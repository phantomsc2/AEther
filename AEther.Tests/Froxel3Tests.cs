using System;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [TestCase(8), TestCase(31), Category("GPU")]
    public void Froxel3AdaptiveRingPreservesUniformEmissionAndConverges(int bands)
    {
        using var shaders=new SceneShaders("Froxel3");
        string path=Path.Combine(shaders.Path,"Scenes","Froxel3.hlsl");
        string source=File.ReadAllText(path).Replace("float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }", """
            #include "../Common/RingLight.hlsl"
            float4 PS(PSin input) : SV_Target
            {
                uint pixel=(uint)input.Position.x;
                float3 p=0;
                switch(pixel/2) {
                    case 0: p=float3(0,0,0);break;
                    case 1: p=float3(2,-3,1);break;
                    case 2: p=float3(4.5,.02,.01);break;
                    case 3: p=float3(.0001,4.5,0);break;
                    case 4: p=float3(-.0001,4.5,0);break;
                    case 5: p=float3(0,0,20);break;
                    case 6: p=float3(-2,1,3);break;
                }
                return LinearOutput((pixel&1)==0 ? AdaptiveRingIncident(p,4.5,.05,FroxelLightCount,0,false) : RingIncident(p,4.5,.05));
            }
            """);
        File.WriteAllText(path,source);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,14,1,bands,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel3");
        var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];
        double coarseError=0,fineError=0;
        for(int pattern=0;pattern<4;pattern++)
        {
            var samples=new double[8*bands];
            for(int side=0;side<2;side++) for(int b=0;b<bands;b++) for(int c=0;c<3;c++)
                samples[4*(side*bands+b)+c]=pattern switch {
                    0 => .01*(c+1),
                    1 => b==bands/2 && side==0 && c==0 ? .03 : 0,
                    2 => c==(b+side)%3 ? .02 : 0,
                    _ => side==0 ? .01*(c+1) : .002*(3-c),
                };
            void Feed(double gain=1)
            {
                renderer.PostAudio(new(samples.Select(v=>gain*v).ToArray(),4*bands,DateTime.UnixEpoch)
                    { AnalysisRow=System.Threading.Interlocked.Increment(ref pointRow) },2);
            }
            foreach(int arcs in new[] {4,8,16,32})
            {
                effect.CloudOptions=new(8,0,arcs);Feed();renderer.Render(false);
                var values=RingReadScene(renderer);
                Assert.That(values.All(float.IsFinite),Is.True);
                double error=0,energy=0;
                for(int p=0;p<7;p++) for(int c=0;c<3;c++)
                {
                    double approx=values[8*p+c],exact=values[8*p+4+c];
                    error+=Math.Abs(approx-exact);energy+=exact;
                    if(pattern==0 || p==0 || p==5)
                        Assert.That(approx,Is.EqualTo(exact).Within(Math.Max(2e-6,exact*.006)), $"Pattern {pattern}, arcs {arcs}, point {p}, color {c}");
                    if(pattern==1 && c!=0) Assert.That(approx,Is.Zero,"Do not invent colors.");
                }
                if(arcs==4) coarseError+=error/energy;
                if(arcs==32) fineError+=error/energy;
                TestContext.Out.WriteLine($"Pattern {pattern}, arcs {arcs}: relative error {error/energy:F5}");
                if(pattern==0)
                {
                    Feed(2);renderer.Render(false);var doubled=RingReadScene(renderer);
                    for(int i=0;i<values.Length;i+=4) for(int c=0;c<3;c++)
                        Assert.That(doubled[i+c],Is.EqualTo(2*values[i+c]).Within(Math.Max(2e-6,values[i+c]*.004)));
                }
            }
        }
        TestContext.Out.WriteLine($"{bands} bands: summed normalized error, 4 arcs={coarseError:F5}, 32 arcs={fineError:F5}");
        Assert.That(fineError,Is.LessThan(coarseError));
        Assert.That(fineError/4,Is.LessThan(.15));
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
                return LinearOutput(AdaptiveRingIncident(p,4.5,.05,FroxelLightCount,n,true));
            }
            """));
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,6,1,8,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel3");
        var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];effect.CloudOptions=new(8,0,32);
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
            for(int c=0;c<3;c++) Assert.That(values[4*pixel+c],Is.EqualTo(expected).Within(Math.Max(1e-6,expected*.03)),"Surface "+pixel);
        }
    }

    [Test, Category("GPU")]
    public void Froxel3CombinesFogWithMovingRasterAndRestoresSettings()
    {
        using var shaders=new SceneShaders("Froxel3");using var window=new HiddenWindow();var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:"Froxel3");
        var effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];
        var markers=new System.Collections.Generic.List<string>();renderer.TransportPassMarker=(s,b)=> { if(b) markers.Add(s); };
        renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointFeed(renderer,0,.04,.02);PointSet(renderer,"Medium.Density",0);renderer.Render(false);
        var surface=renderer.ReadPixels(false);
        Assert.That(surface.Where((_,i)=>i%4==0).Count(v=>v>20),Is.GreaterThan(1000));
        Assert.That(surface.Where((_,i)=>i%4==1 || i%4==2),Is.All.Zero);
        renderer.Render(false);Assert.That(renderer.ReadPixels(false),Is.EqualTo(surface));
        clock.Frame=60;renderer.Render(false);Assert.That(renderer.ReadPixels(false).SequenceEqual(surface),Is.False);
        effect.CloudOptions=new(32,0,16);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointFeed(renderer,0,.04,.02);PointSet(renderer,"Medium.Density",.159f);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4==0).Any(v=>v>20),Is.True);
        effect.CloudOptions=new(32,1024,16);renderer.Render(false);
        Assert.That(markers,Does.Contain("Ring spectrum prefix"));Assert.That(markers,Does.Contain("Froxel raster"));
        Assert.That(markers,Does.Not.Contain("Froxel lights"));
        effect.CloudOptions=new(32,1024,7);renderer.Render(false);
        Assert.That(effect.CloudOptions.LightingArcs,Is.EqualTo(16),"Invalid configuration rolls back.");
        renderer.RestartAudio(new(new Domain(30,12,31),60));PointFeed(renderer,2,.03,.01);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4==2).Any(v=>v>10),Is.True);
        PointFeed(renderer,-1);renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        renderer.Resize(1281,721);renderer.Render(false);Assert.That(effect.Dimensions,Is.EqualTo((161,91,32)));
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);effect=(Froxel3Effect)renderer.ActiveShader!.Effects[0];
        Assert.That(effect.CloudOptions,Is.EqualTo(new Froxel3Options(32,1024,16)));
        File.AppendAllText(Path.Combine(shaders.Path,"Scenes","Froxel3.hlsl"),"\nINVALID HLSL");
        Assert.That(renderer.ReloadShaders(),Is.False);Assert.DoesNotThrow(()=>renderer.Render(false));
        renderer.SelectScene(null);Assert.That(effect.Dimensions,Is.EqualTo((0,0,0)));
    }
}

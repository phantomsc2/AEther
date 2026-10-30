using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [Test,Category("GPU")]
    public void Froxel4LineDirectionsAgreeWithLambertQuadrature()
    {
        using var shaders=new SceneShaders("Froxel4");
        LineDebugShader(shaders,"""
            float4 PS(PSin input):SV_Target {
                LineLight light;light.Origin=0;light.Axis=float3(0,0,1);
                light.Softening=.05;light.Intensity=1;light.Padding0=0;light.Padding1=0;
                float3 p=float3(1,0,0);
                return LinearOutput(float3(LineWeight(p,light)*LineDiffuseAverage(p,light,float3(-1,0,0)),
                    LineDiffuseAverage(p,light,float3(1,0,0)),LineWeight(p,light)/(4*LinePi)));
            }
            """);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1,1,8,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel4");
        ((Froxel4Effect)renderer.ActiveShader!.Effects[0]).CloudOptions=new(8,0);
        renderer.Render(false);var actual=RingReadScene(renderer);
        double weight=0,lambert=0;
        for(int j=0;j<65536;j++)
        {
            double theta=-Math.PI/2+Math.PI*(j+.5)/65536;
            double c=Math.Cos(theta),s=Math.Sin(theta);
            double cosine=c,w=(Math.PI/65536)/((1+.05*.05)*c*c+s*s);
            weight+=w;lambert+=w*cosine;
        }
        Assert.That(actual[0],Is.EqualTo(lambert).Within(lambert*.02));
        Assert.That(actual[1],Is.Zero);
        Assert.That(actual[2],Is.EqualTo(weight/(4*Math.PI)).Within(weight*.001));
    }
    static double LineReference(Vector3 p,double soft=.05)
    {
        // World-axis coordinate u=tan(theta), including the Jacobian. This
        // numerical integral does not use the closed pi/b expression.
        const int n=65536;double sum=0;
        for(int j=0;j<=n;j++)
        {
            double theta=-Math.PI/2+Math.PI*j/n,c=Math.Cos(theta),s=Math.Sin(theta);
            double value=1/((p.X*p.X+p.Y*p.Y+soft*soft)*c*c+s*s);
            sum+=(j==0 || j==n ? 1 : j%2==0 ? 2 : 4)*value;
        }
        return sum*Math.PI/(3*n);
    }
    static void LineDebugShader(SceneShaders shaders,string function)
    {
        string path=Path.Combine(shaders.Path,"Scenes","Froxel4.hlsl");
        File.WriteAllText(path,File.ReadAllText(path).Replace(
            "float4 PS(PSin input) : SV_Target { return LinearOutput(FroxelComposite(input)); }",function));
    }
    [Test,Category("GPU")]
    public void Froxel4LineIntegralAndCameraPeakMatchNumericalReference()
    {
        var cases=new[] {
            (A:new Vector3(.01f,0,-.5f),B:new Vector3(.01f,0,.7f)),
            (A:new Vector3(-.5f,.01f,0),B:new Vector3(.7f,.01f,0)),
            (A:new Vector3(1,0,11),B:new Vector3(1,0,13)),
            (A:new Vector3(2,1,20),B:new Vector3(2.2f,1.1f,21)),
            (A:new Vector3(0,0,12),B:new Vector3(0,0,12)),
        };
        using var shaders=new SceneShaders("Froxel4");
        string function="float4 PS(PSin input):SV_Target {float3 a=0,b=0;switch((uint)input.Position.x){";
        string V(Vector3 p)=>$"float3({RingNumber(p.X)},{RingNumber(p.Y)},{RingNumber(p.Z)})";
        for(int i=0;i<cases.Length;i++) function+=$"case {i}:a={V(cases[i].A)};b={V(cases[i].B)};break;";
        function+="}LineLight light;light.Origin=0;light.Axis=float3(0,0,1);light.Softening=.05;light.Intensity=1;light.Padding0=0;light.Padding1=0;float pointWeight=LineWeight(a,light);float segment=LineSegmentAverage(a,b,light);light.Origin+=light.Axis*1000;return LinearOutput(float3(pointWeight,segment,LineWeight(a,light)));}";
        LineDebugShader(shaders,function);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,cases.Length,1,8,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel4");
        ((Froxel4Effect)renderer.ActiveShader!.Effects[0]).CloudOptions=new(8,0);
        renderer.Render(false);var values=RingReadScene(renderer);
        for(int i=0;i<cases.Length;i++)
        {
            double point=LineReference(cases[i].A),sum=0;
            // Closed point formula is independently validated above by quadrature.
            for(int j=0;j<32768;j++)
            {
                var p=Vector3.Lerp(cases[i].A,cases[i].B,(j+.5f)/32768);
                double b=Math.Sqrt(p.X*p.X+p.Y*p.Y+.05*.05);
                sum+=Math.PI/b;
            }
            double expected=sum/32768;
            TestContext.Out.WriteLine($"Line case {i}: point {values[4*i]:F5}/{point:F5}, segment {values[4*i+1]:F5}/{expected:F5}");
            Assert.That(values[4*i],Is.EqualTo(point).Within(Math.Max(1e-5,point*.002)));
            Assert.That(values[4*i+1],Is.EqualTo(expected).Within(Math.Max(1e-5,expected*.003)));
            Assert.That(values[4*i+2],Is.EqualTo(values[4*i]),"Translating the origin along its axis cannot change an infinite line.");
        }
    }
    [TestCase(5),TestCase(16),TestCase(31),Category("GPU")]
    public void Froxel4PreparesExactStereoBinLines(int bands)
    {
        using var shaders=new SceneShaders("Froxel4");
        LineDebugShader(shaders,"""
            float4 PS(PSin input):SV_Target {
                LineLight light=LineLights[(uint)input.Position.x];
                return LinearOutput(input.Position.y<1 ? light.Intensity : float3(light.Origin.xy+4,light.Origin.z));
            }
            """);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,2*bands,2,bands,60,shaderDirectory:shaders.Path,timeProvider:new FroxelClock(),initialScene:"Froxel4");
        var effect=(Froxel4Effect)renderer.ActiveShader!.Effects[0];effect.CloudOptions=new(8,0);
        var samples=new double[8*bands];
        for(int side=0;side<2;side++) for(int bin=0;bin<bands;bin++) samples[4*(side*bands+bin)+(side+bin)%3]=.01*(bin+1)/bands;
        void Feed(double scale)
        {
            renderer.PostAudio(new(samples.Select(v=>v*scale).ToArray(),4*bands,DateTime.UnixEpoch)
                {AnalysisRow=System.Threading.Interlocked.Increment(ref pointRow)},2);
        }
        Feed(1);renderer.Render(false);var values=RingReadScene(renderer);
        Assert.That(effect.LineCount,Is.EqualTo(2*bands));
        double gain=1000*Math.Pow(2,16*(.4-.5));
        for(int side=0;side<2;side++) for(int bin=0;bin<bands;bin++)
        {
            int light=side*bands+bin;
            for(int c=0;c<3;c++)
            {
                double expected=samples[4*light+c]*gain*Math.PI/bands;
                Assert.That(values[4*light+c],Is.EqualTo(expected).Within(Math.Max(1e-6,expected*.004)));
            }
            double angle=(side==0 ? -1 : 1)*(bin+.5)*Math.PI/bands;
            Assert.That(values[4*(2*bands+light)],Is.EqualTo(4+2.5*Math.Sin(angle)).Within(.006));
            Assert.That(values[4*(2*bands+light)+1],Is.EqualTo(4-2.5*Math.Cos(angle)).Within(.006));
            Assert.That(values[4*(2*bands+light)+2],Is.Zero);
        }
        Feed(2);renderer.Render(false);var doubled=RingReadScene(renderer);
        for(int i=0;i<2*bands;i++) for(int c=0;c<3;c++)
            Assert.That(doubled[4*i+c],Is.EqualTo(2*values[4*i+c]).Within(Math.Max(1e-6,values[4*i+c]*.004)));
    }
    [Test,Category("GPU")]
    public void Froxel4ParallelLightingAndShellLifecycle()
    {
        using var shaders=new SceneShaders("Froxel4");using var window=new HiddenWindow();var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,256,144,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:"Froxel4");
        var effect=(Froxel4Effect)renderer.ActiveShader!.Effects[0];effect.CloudOptions=new(32,4096);
        var markers=new System.Collections.Generic.List<string>();renderer.TransportPassMarker=(s,b)=> {if(b) markers.Add(s);};
        renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointFeed(renderer,0,.04,.02);renderer.Render(false);var lit=renderer.ReadPixels(false);
        Assert.That(lit.Where((_,i)=>i%4==0).Any(v=>v>20),Is.True);
        Assert.That(lit.Where((_,i)=>i%4==1 || i%4==2),Is.All.Zero);
        renderer.Render(false);Assert.That(renderer.ReadPixels(false),Is.EqualTo(lit));
        Assert.That(markers.IndexOf("Froxel material"),Is.LessThan(markers.IndexOf("Froxel line lighting")));
        Assert.That(markers.IndexOf("Froxel line lighting"),Is.LessThan(markers.IndexOf("Froxel integration")));
        Assert.That(markers,Does.Contain("Line light preparation"));Assert.That(markers,Does.Contain("Froxel raster"));
        PointSet(renderer,"Medium.Density",0);renderer.Render(false);var surface=renderer.ReadPixels(false);
        Assert.That(surface.Where((_,i)=>i%4==0).Any(v=>v>20),Is.True);
        effect.CloudOptions=new(32,0);renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointSet(renderer,"Medium.Density",.36f);renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4==0).Any(v=>v>20),Is.True);
        effect.CloudOptions=new(32,4096);PointSet(renderer,"Medium.Speed",0);clock.Frame=120;
        PointFeed(renderer,0,.04,.02);renderer.Render(false);Assert.That(renderer.ReadPixels(false).SequenceEqual(lit),Is.False);
        Assert.That(effect.Parameters!.Items.Single(p=>p.Name=="Medium").Children.Any(p=>p.Name=="Anisotropy"),Is.False);
        Assert.That(effect.Parameters.Items.Single(p=>p.Name=="Tube").Children.Any(p=>p.Name=="Length"),Is.False);
        Assert.That(RingReadScene(renderer).All(float.IsFinite),Is.True);
        renderer.RestartAudio(new(new Domain(30,12,31),60));PointFeed(renderer,2,.03,.01);renderer.Render(false);
        Assert.That(effect.LineCount,Is.EqualTo(62));Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4==2).Any(v=>v>10),Is.True);
        PointFeed(renderer,-1);renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        effect.CloudOptions=new(7,4096);renderer.Render(false);Assert.That(effect.CloudOptions.DepthSlices,Is.EqualTo(32));
        renderer.Resize(257,145);renderer.Render(false);Assert.That(effect.Dimensions,Is.EqualTo((33,19,32)));
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);effect=(Froxel4Effect)renderer.ActiveShader!.Effects[0];
        Assert.That(effect.CloudOptions,Is.EqualTo(new Froxel4Options(32,4096)));
        File.AppendAllText(Path.Combine(shaders.Path,"Scenes","Froxel4.hlsl"),"\nINVALID HLSL");
        Assert.That(renderer.ReloadShaders(),Is.False);Assert.DoesNotThrow(()=>renderer.Render(false));
        renderer.SelectScene(null);Assert.That(effect.Dimensions,Is.EqualTo((0,0,0)));Assert.That(effect.LineCount,Is.Zero);
    }
}

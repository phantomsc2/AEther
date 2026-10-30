using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using AEther.Vortice;
using Vortice.Direct3D11;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    static string RingNumber(double x) => x.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
    static double RingQuadrature(Vector3 p,double radius,double soft,double start,double span)
    {
        // Independent composite Simpson integration in world space, not the analytic substitution.
        const int n=65536; double sum=0;
        for(int i=0;i<=n;i++)
        {
            double t=start+span*i/n;
            double x=p.X-radius*Math.Sin(t),y=p.Y+radius*Math.Cos(t);
            double value=radius/(x*x+y*y+p.Z*p.Z+soft*soft);
            sum+=(i==0 || i==n ? 1 : i%2==0 ? 2 : 4)*value;
        }
        return sum*span/(3*n);
    }
    static float[] RingReadScene(Renderer renderer)
    {
        var texture=PointField<ID3D11Texture2D>(PointField<object>(renderer,"output"),"scene");
        var device=PointField<ID3D11Device>(renderer,"device");
        var context=PointField<ID3D11DeviceContext>(renderer,"context");
        var desc=texture.Description;desc.Usage=ResourceUsage.Staging;desc.BindFlags=BindFlags.None;desc.CPUAccessFlags=CpuAccessFlags.Read;
        using var staging=device.CreateTexture2D(desc);context.CopyResource(staging,texture);
        var map=context.Map(staging,0,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes=new byte[desc.Width*desc.Height*8];
            for(int y=0;y<desc.Height;y++) Marshal.Copy(map.DataPointer+y*(int)map.RowPitch,bytes,y*(int)desc.Width*8,(int)desc.Width*8);
            return MemoryMarshal.Cast<byte,Half>(bytes).ToArray().Select(v=>(float)v).ToArray();
        }
        finally { context.Unmap(staging,0); }
    }
    [Test,Category("GPU")]
    public void RingArcGpuMatchesIndependentQuadratureAndSubdivision()
    {
        var cases=new[] {
            (P:new Vector3(0,0,0),R:4.5,A:.05,S:-Math.PI,D:Math.PI),
            (P:new Vector3(0,0,3),R:4.5,A:.01,S:2.9,D:.2),
            (P:new Vector3(4.5f,.001f,.002f),R:4.5,A:.01,S:1.4,D:.3),
            (P:new Vector3(-4.49f,-.01f,0),R:4.5,A:.05,S:-1.8,D:.4),
            (P:new Vector3(1,4,.1f),R:4.5,A:.01,S:2.8,D:.7),
            (P:new Vector3(2,-3,5),R:4.5,A:.5,S:-3.1,D:3.0),
            (P:new Vector3(4.5f,0,0),R:4.5,A:.01,S:0.0,D:Math.PI),
            (P:new Vector3(0,4.5f,0),R:4.5,A:.05,S:3.0,D:.28),
        };
        using var shaders=new SceneShaders("Spectrum");
        string source="#include \"../Common/Inputs.hlsl\"\n#include \"../Common/RingLight.hlsl\"\nfloat4 PS(PSin input):SV_Target { float3 p=0; float r=1,a=1,s=0,d=1; switch((uint)input.Position.x) {";
        for(int i=0;i<cases.Length;i++)
        {
            var c=cases[i];source+=$"case {i}: p=float3({RingNumber(c.P.X)},{RingNumber(c.P.Y)},{RingNumber(c.P.Z)});r={RingNumber(c.R)};a={RingNumber(c.A)};s={RingNumber(c.S)};d={RingNumber(c.D)};break;";
        }
        source+="} float w=RingArcWeight(p,r,a,s,d);float split=RingArcWeight(p,r,a,s,d*.5)+RingArcWeight(p,r,a,s+d*.5,d*.5);float full=RingArcWeight(p,r,a,0,3.14159265359)+RingArcWeight(p,r,a,3.14159265359,3.14159265359); return LinearOutput(float3(w,split,full));}";
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"),source);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,cases.Length,1,8,60,shaderDirectory:shaders.Path,initialScene:"Spectrum");
        renderer.Render(false);var values=RingReadScene(renderer);
        for(int i=0;i<cases.Length;i++)
        {
            var c=cases[i];double expected=RingQuadrature(c.P,c.R,c.A,c.S,c.D);
            Assert.That(values[4*i],Is.EqualTo(expected).Within(Math.Max(1e-5,expected*.002)),"Arc "+i);
            Assert.That(values[4*i+1],Is.EqualTo(values[4*i]).Within(Math.Max(1e-5,expected*.002)),"Subdivision "+i);
            double rho=Math.Sqrt(c.P.X*c.P.X+c.P.Y*c.P.Y),axial=c.P.Z*c.P.Z+c.A*c.A;
            double full=2*Math.PI*c.R/Math.Sqrt(((c.R-rho)*(c.R-rho)+axial)*((c.R+rho)*(c.R+rho)+axial));
            Assert.That(values[4*i+2],Is.EqualTo(full).Within(full*.002),"Full ring "+i);
        }
    }
    [TestCase(5),TestCase(8),TestCase(31),Category("GPU")]
    public void RingSpectrumUsesExactStereoBinsAndLinearStbColors(int bands)
    {
        using var shaders=new SceneShaders("Spectrum");
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"),"""
            #include "../Common/Inputs.hlsl"
            #include "../Common/RingLight.hlsl"
            float4 PS(PSin i):SV_Target { return LinearOutput(RingIncident(float3(-3,1,.2),4.5,.05)); }
            """);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,1,1,bands,60,shaderDirectory:shaders.Path,timeProvider:new PointClock(),initialScene:"Spectrum");
        for(int channel=0;channel<2;channel++) for(int component=0;component<3;component++)
        {
            int bin=bands/2;
            var samples=new double[8*bands];samples[4*(channel*bands+bin)+component]=.025;
            renderer.PostAudio(new(samples,4*bands,DateTime.UnixEpoch) { AnalysisRow=System.Threading.Interlocked.Increment(ref pointRow) },2);
            renderer.Render(false);var actual=RingReadScene(renderer);
            double span=Math.PI/bands,start=channel==0 ? -(bin+1)*span : bin*span;
            double expected=.025*RingQuadrature(new(-3,1,.2f),4.5,.05,start,span);
            for(int c=0;c<3;c++) Assert.That(actual[c],Is.EqualTo(c==component ? expected : 0).Within(Math.Max(1e-6,expected*.003)));
            samples[4*(channel*bands+bin)+component]*=2;
            renderer.PostAudio(new(samples,4*bands,DateTime.UnixEpoch) { AnalysisRow=System.Threading.Interlocked.Increment(ref pointRow) },2);
            renderer.Render(false);Assert.That(RingReadScene(renderer)[component],Is.EqualTo(2*actual[component]).Within(expected*.004));
        }
    }
    [Test,Category("GPU")]
    public void Froxel2HasOnlyVolumeStorageAndDeterministicTimeJitter()
    {
        using var shaders=new SceneShaders("Froxel2");using var window=new HiddenWindow();var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,128,72,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:shaders.Scene);
        var effect=(Froxel2Effect)renderer.ActiveShader!.Effects[0];
        var markers=new System.Collections.Generic.List<string>();renderer.TransportPassMarker=(s,b)=> {if(b) markers.Add(s);};
        renderer.Render(false);Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        foreach(string field in new[] {"lights","raster"})
            Assert.That(typeof(FroxelEffect).GetField(field,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(effect),Is.Null);
        Assert.That(effect.DepthOptions.DepthSlices,Is.EqualTo(86));
        PointFeed(renderer,0,.04,.02);renderer.Render(false);var original=renderer.ReadPixels(false);
        Assert.That(original.Where((_,i)=>i%4==0).Any(v=>v>20),Is.True);
        Assert.That(original.Where((_,i)=>i%4==1 || i%4==2),Is.All.Zero);
        renderer.Render(false);Assert.That(renderer.ReadPixels(false).SequenceEqual(original),Is.True);
        PointSet(renderer,"Medium.Speed",0);renderer.Render(false);var frozen=ReadFroxelVolume(renderer,effect.IntegratedTexture);
        clock.Frame=3;PointFeed(renderer,0,.04,.02);renderer.Render(false);
        Assert.That(ReadFroxelVolume(renderer,effect.IntegratedTexture).SequenceEqual(frozen),Is.False,"Jitter animates even with stationary material.");
        renderer.RestartAudio(new(new Domain(30,12,16),60));
        PointFeed(renderer,2,.03,.01);renderer.Render(false);
        Assert.That(renderer.Audio.Bands,Is.EqualTo(16));
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4==2).Any(v=>v>10),Is.True);
        PointFeed(renderer,-1);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointFeed(renderer,1,.03,.02);PointSet(renderer,"Medium.Density",0);renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointSet(renderer,"Medium.Density",.159f);
        Assert.That(markers,Does.Contain("Froxel ring injection"));Assert.That(markers,Does.Not.Contain("Froxel raster"));Assert.That(markers,Does.Not.Contain("Froxel lights"));
        effect.DepthOptions=new(32);renderer.Resize(137,81);renderer.Render(false);Assert.That(effect.Dimensions,Is.EqualTo((18,11,32)));
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);effect=(Froxel2Effect)renderer.ActiveShader!.Effects[0];
        Assert.That(effect.DepthOptions.DepthSlices,Is.EqualTo(32));
        File.AppendAllText(Path.Combine(shaders.Path,"Scenes","Froxel2.hlsl"),"\nINVALID HLSL");
        Assert.That(renderer.ReloadShaders(),Is.False);Assert.DoesNotThrow(()=>renderer.Render(false));
        renderer.SelectScene(null);Assert.That(effect.Dimensions,Is.EqualTo((0,0,0)));
    }
}

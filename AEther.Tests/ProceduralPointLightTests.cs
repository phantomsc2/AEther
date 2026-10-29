using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using AEther.Vortice;
using NUnit.Framework;
using Vortice.Direct3D11;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [Test, Category("GPU")]
    public void ProceduralRingsUseIndependentLiveStbAndGpuDefinedPositions()
    {
        using var shaders = new SceneShaders("MandelLight");
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 96, 54, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: "MandelLight");
        var passes = new List<string>();
        renderer.TransportPassMarker = (name, begin) => { if (begin) passes.Add(name); };
        for (int component = 0; component < 3; component++)
        {
            PointFeed(renderer, component);
            renderer.Render(false);
            var records = PointRecords(renderer);
            Assert.That(records.Length, Is.EqualTo(180));
            for (int ring = 0; ring < 3; ring++)
            {
                for (int i = 0; i < 60; i++)
                {
                    float angle = 2*MathF.PI*i/60, x = MathF.Sin(angle), y = MathF.Cos(angle);
                    Vector3 expected = 5.7f*(ring == 0 ? new Vector3(x,0,y) : ring == 1 ? new Vector3(x,y,0) : new Vector3(0,x,y));
                    Assert.That(Vector3.Distance(records[ring*60+i].Position, expected), Is.LessThan(.0001));
                    Assert.That(records[ring*60+i].Intensity.Length() > 0, Is.EqualTo(ring == component));
                }
            }
            // Quarter-turn selects pure right stereo at f=.5. Intensity is linear in the live component.
            float red = .5f+.5f*MathF.Cos(2*MathF.PI*(.5f*.85f+.04f));
            Assert.That(records[component*60+15].Intensity.X, Is.EqualTo(12000*.04*red).Within(.002));
            Assert.That(records[component*60+45].Intensity.X, Is.EqualTo(12000*.01*red).Within(.002));
            Assert.That(renderer.ReadPixels(false).Where((_, i) => i%4 != 3).Any(x => x > 10), Is.True);
        }
        PointFeed(renderer, -1); renderer.Render(false);
        Assert.That(PointRecords(renderer).All(x => x.Intensity == Vector3.Zero), Is.True);
        Assert.That(renderer.ReadPixels(false).Where((_, i) => i%4 != 3), Is.All.Zero);
        PointSet(renderer, "Sizes.PointLights", 0u); PointFeed(renderer, 0); renderer.Render(false);
        Assert.That(PointRecords(renderer), Is.Empty);
        Assert.That(renderer.ReadPixels(false).Where((_, i) => i%4 != 3), Is.All.Zero);
        Assert.That(passes, Does.Contain("Raster lights prepare"));
        Assert.That(passes, Does.Contain("Raster surface"));
        Assert.That(renderer.ActiveShader!.Parameters!.Items.Any(x => x.Name == "Quality"), Is.False);
    }

    [Test, Category("GPU")]
    public void PointLightSetupOwnsCountAndPropertiesRemainShaderDefined()
    {
        using var shaders = new SceneShaders("MandelLight");
        string file = Path.Combine(shaders.Path, "Scenes", "MandelLight.hlsl");
        string source = File.ReadAllText(file);
        int begin = source.IndexOf("PointLight EvaluateLight(uint index)", StringComparison.Ordinal);
        int end = source.IndexOf("float4 PS(", begin, StringComparison.Ordinal);
        source = source[..begin] + """
            PointLight EvaluateLight(uint index)
            {
                PointLight light;
                light.Position=float3(index*.001,10*Lights.Radius,0);
                light.Intensity=float3(Spectrum0.SampleLevel(Linear,float2(.5,.25),0).r,0,0);
                return light;
            }
            """ + source[end..];
        File.WriteAllText(file, Regex.Replace(source, @"\bLights\b", "Emitters"));
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 64, 36, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: "MandelLight");
        PointSet(renderer, "Sizes.PointLights", 700u); PointFeed(renderer, 0); renderer.Render(false);
        var records = PointRecords(renderer);
        Assert.That(records.Length, Is.EqualTo(700));
        Assert.That(records[^1].Position.X, Is.EqualTo(.699).Within(.00001));
        Assert.That(records[0].Position.Y, Is.EqualTo(5.7).Within(.00001));
        PointSet(renderer, "Sizes.PointLights", 7u); PointSet(renderer, "Emitters.Radius", .2f);
        PointFeed(renderer, 0); renderer.Render(false);
        records = PointRecords(renderer);
        Assert.That(records.Length, Is.EqualTo(7));
        Assert.That(records.All(x => Math.Abs(x.Position.Y-2)<.00001), Is.True);
        PointSet(renderer, "Sizes.PointLights", 0u); renderer.Render(false);
        Assert.That(PointRecords(renderer), Is.Empty);
        Assert.That(renderer.ReadPixels(false).Where((_, i) => i%4 != 3), Is.All.Zero);
    }

    [TestCase(false), TestCase(true), Category("GPU")]
    public void ProceduralPointAirlightMatchesIndependentQuadrature(bool surfaceClipped)
    {
        using var shaders = new SceneShaders("MandelLight");
        if (surfaceClipped)
        {
            string file = Path.Combine(shaders.Path, "Scenes", "MandelLight.hlsl");
            string source = File.ReadAllText(file);
            int begin = source.IndexOf("GeometryVertex EvaluateGeometry", StringComparison.Ordinal);
            int end = source.IndexOf("static const float SceneLightGain", begin, StringComparison.Ordinal);
            source = source[..begin] + """
                GeometryVertex EvaluateGeometry(uint vertex, uint instance)
                {
                    float2 corners[6]={float2(-1,-1),float2(-1,1),float2(1,-1),
                        float2(1,-1),float2(-1,1),float2(1,1)};
                    ProjectionCamera camera=SceneCamera();
                    float3 origin=camera.Origin,forward=camera.Forward,right=camera.Right,up=camera.Up;
                    GeometryVertex v;
                    v.World=origin+24*forward+100*(corners[vertex].x*right+corners[vertex].y*up);
                    v.Normal=-forward; v.Albedo=0;
                    return v;
                }
                """ + source[end..];
            File.WriteAllText(file, source.Replace("uint2(36,128)", "uint2(6,1)"));
        }
        const int width = 64, height = 36;
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, width, height, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: "MandelLight");
        PointSet(renderer, "View.CameraSpeed", 0f); PointSet(renderer, "View.CameraDistance", .6f);
        PointSet(renderer, "Sizes.PointLights", 3u); PointSet(renderer, "Lights.Gain", .5f);
        if (!surfaceClipped) PointSet(renderer, "Sizes.Geometry.y", 0u); PointSet(renderer, "Air.Density", .4f);
        foreach (char axis in "xyz") { PointSet(renderer, "Air.Scattering."+axis, .5f); PointSet(renderer, "Air.Absorption."+axis, 0f); }
        PointRender(renderer, () => PointFeed(renderer, 0, .0225, .0225));
        byte[] pixels = renderer.ReadPixels(false);
        Vector3 forward = Vector3.Normalize(new(-9,-6,13.5f)), origin = -24*forward;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY)), up = Vector3.Cross(right,forward);
        Vector3 light = new(0,0,5.7f);
        double sigma = .3*.4*.4*.5, red = .5+.5*Math.Cos(2*Math.PI*(.85+.04)), squared = 0;
        int count = 0;
        for (int y = 1; y < height; y += 5) for (int x = 1; x < width; x += 7)
        {
            Vector3 direction = Vector3.Normalize(2*forward+(2*(x+.5f)/width-1)*width/height*right+(1-2*(y+.5f)/height)*up);
            double stop = 24/Vector3.Dot(direction,forward), sum = 0;
            const int steps = 8192;
            for (int k = 0; k < steps; k++)
            {
                double u = k/(double)steps, t = surfaceClipped ? stop*u : 32*u/(1-u);
                double jacobian = surfaceClipped ? stop : 32/((1-u)*(1-u));
                double dx=light.X-origin.X-t*direction.X,dy=light.Y-origin.Y-t*direction.Y,dz=light.Z-origin.Z-t*direction.Z;
                double r2=dx*dx+dy*dy+dz*dz,r=Math.Sqrt(r2),mu=(dx*direction.X+dy*direction.Y+dz*direction.Z)/r;
                double value=sigma*3000*.0225*red*3*(1+mu*mu)/(16*Math.PI)*Math.Exp(-sigma*(t+r))/r2*jacobian;
                sum+=(k==0 ? 1 : k%2==0 ? 2 : 4)*value;
            }
            // Endpoint contribution for the finite Simpson integral.
            if (surfaceClipped)
            {
                Vector3 delta=light-origin-(float)stop*direction;
                double r=delta.Length(),mu=Vector3.Dot(delta,direction)/r;
                sum+=sigma*3000*.0225*red*3*(1+mu*mu)/(16*Math.PI)*Math.Exp(-sigma*(stop+r))/(r*r)*stop;
            }
            double mapped=1-Math.Exp(-sum/(3*steps)*Math.Pow(2,-.6));
            double expected=mapped<=.0031308 ? 12.92*mapped : 1.055*Math.Pow(mapped,1/2.4)-.055;
            squared+=Math.Pow(pixels[4*(y*width+x)]/255.0-expected,2); count++;
        }
        double rmse=Math.Sqrt(squared/count);
        TestContext.Progress.WriteLine($"Point LUT, surfaceClipped={surfaceClipped}: display-RGB RMSE={rmse:F6}");
        Assert.That(rmse, Is.LessThan(.005));
    }

    [Test, Category("GPU")]
    public void ProceduralPointPassRebuildsLookupAndSurvivesEditsResizeReloadAndSceneSwitches()
    {
        using var shaders = new SceneShaders("MandelLight");
        File.Copy(Path.Combine(ShaderDirectory,"Scenes","Spectrum.hlsl"),Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"));
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle, 81, 47, 32, 60,
            shaderDirectory: shaders.Path, timeProvider: new PointClock(), initialScene: "MandelLight");
        PointRender(renderer, () => PointFeed(renderer, 0));
        var lookup = PointField<object>(PointField<object>(renderer,"rasterLight"),"lookup");
        byte[] original = renderer.ReadPixels(false);
        PointSet(renderer,"Air.Density",.4f); PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.Not.EqualTo(original), "Coefficient edits must rebuild before shading.");
        PointRender(renderer, () => PointFeed(renderer,0));
        PointSet(renderer,"Air.Density",.65f); PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
        renderer.Resize(97,55); PointFeed(renderer,0); renderer.Render(false);
        renderer.Resize(81,47); PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
        Assert.That(renderer.ReloadShaders(), Is.True, renderer.ShaderError);
        PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
        renderer.SelectScene("Spectrum"); PointFeed(renderer,0); renderer.Render(false);
        Assert.That(PointField<object>(PointField<object>(renderer,"rasterLight"),"lookup"), Is.SameAs(lookup));
        renderer.SelectScene("MandelLight"); PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
    }

    static long pointRow;
    static void PointFeed(Renderer renderer, int component, double left = .01, double right = .04)
    {
        int bands=renderer.Audio.Bands; var spectrum=new double[8*bands];
        if (component>=0) for (int c=0;c<2;c++) for (int i=0;i<bands;i++) spectrum[4*(c*bands+i)+component]=c==0 ? left : right;
        renderer.PostAudio(new(spectrum,4*bands,DateTime.UnixEpoch) {AnalysisRow=Interlocked.Increment(ref pointRow)},2);
    }
    static void PointRender(Renderer renderer, Action feed)
    {
        feed(); renderer.Render(false);
    }
    static ShaderParameter PointFind(Renderer renderer, string path)
    {
        var items=renderer.ActiveShader!.Parameters!.Items; ShaderParameter result=null!;
        foreach (string part in path.Split('.')) { result=items.Single(x=>x.Name==part); items=result.Children; }
        return result;
    }
    static void PointSet(Renderer renderer, string path, float value) => renderer.ActiveShader!.Parameters!.SetFloat(PointFind(renderer,path),value);
    static void PointSet(Renderer renderer, string path, uint value) => renderer.ActiveShader!.Parameters!.SetUnsigned(PointFind(renderer,path),value);
    static void PointSet(Renderer renderer, string path, bool value) => renderer.ActiveShader!.Parameters!.SetBoolean(PointFind(renderer,path),value);
    static T PointField<T>(object value, string name) => (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(value)!;
    static (Vector3 Position, Vector3 Intensity)[] PointRecords(Renderer renderer)
    {
        var device=PointField<ID3D11Device>(renderer,"device"); var context=PointField<ID3D11DeviceContext>(renderer,"context");
        var transport=PointField<RasterLight>(renderer,"rasterLight");
        byte[] Read(string field)
        {
            using var resource=transport.LightsView.Resource;
            using var source=resource.QueryInterface<ID3D11Buffer>();
            using var staging=device.CreateBuffer(new BufferDescription {ByteWidth=source.Description.ByteWidth,Usage=ResourceUsage.Staging,CPUAccessFlags=CpuAccessFlags.Read});
            context.CopyResource(staging,source);
            var map=context.Map(staging,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
            try { var bytes=new byte[source.Description.ByteWidth]; Marshal.Copy(map.DataPointer,bytes,0,bytes.Length); return bytes; }
            finally { context.Unmap(staging,0); }
        }
        int count=(int)transport.LightCount; byte[] records=Read("lightsView");
        return Enumerable.Range(0,count).Select(i=>(Vector(i*24),Vector(i*24+12))).ToArray();
        Vector3 Vector(int at) => new(BitConverter.ToSingle(records,at),BitConverter.ToSingle(records,at+4),BitConverter.ToSingle(records,at+8));
    }
    sealed class PointClock : TimeProvider { public override long TimestampFrequency => 60; public override long GetTimestamp() => 0; }
}

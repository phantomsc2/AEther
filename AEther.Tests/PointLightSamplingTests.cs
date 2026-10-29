using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AEther.Vortice;
using NUnit.Framework;
using Vortice.Direct3D11;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [TestCase("varied",false), TestCase("varied",true), TestCase("identical",false)]
    [TestCase("depth-edge",true), TestCase("near-core",false)]
    [TestCase("chromatic",false), TestCase("underflow",false), TestCase("singular",false), Category("GPU")]
    public void SampledAirlightAgreesWithTheLinearRadianceReference(string fixture, bool finite)
    {
        using var shaders=new SceneShaders("MandelLightv5");
        string file=Path.Combine(shaders.Path,"Scenes","MandelLightv5.hlsl"),source=File.ReadAllText(file);
        int begin=source.IndexOf("uint LightCount()",StringComparison.Ordinal),end=source.IndexOf("float4 PS(",begin,StringComparison.Ordinal);
        string lights="""
            uint LightCount() { return Lights.Enabled ? 64 : 0; }
            PointLight EvaluateLight(uint i)
            {
                PointLight light;
                light.Position=float3(.5+(i%8)*.2,-.5+(i/8)*.15,1+(i%5));
                light.Intensity=(.1+.03*(i%7))*float3(1+(i%3),1+(i%5),1+(i%2));
                FIXTURE
                return light;
            }
            """;
        string special=fixture switch
        {
            "identical" => "light.Position=float3(1,0,3); light.Intensity=1;",
            "chromatic" => "light.Intensity=float3(.2,0,.2); if(i==63) {light.Position=float3(4,0,1000); light.Intensity=float3(0,10000,0);}",
            "underflow" => "light.Position=float3(4,0,1000); light.Intensity=float3(0,1,0);",
            "singular" => "light.Position=float3(0,0,2); light.Intensity=1;",
            "near-core" => "if(i==63) {light.Position=float3(.0001,0,3); light.Intensity=1;}",
            _ => "",
        };
        File.WriteAllText(file,source[..begin]+lights.Replace("FIXTURE",special)+"""
            float4 PS(PSin input) : SV_Target
            {
                int2 pixel=(int2)input.Position.xy;
                return Display(PointSurface.Load(int3(pixel,0)).rgb+PointAirlight.Load(int3(pixel,0)).rgb);
            }
            """);
        string common=Path.Combine(shaders.Path,"Common","PointRasterAirlight.hlsl");
        source=File.ReadAllText(common); begin=source.IndexOf("void PointRay(",StringComparison.Ordinal);
        end=source.IndexOf("float3 PointAirlightContribution(",begin,StringComparison.Ordinal);
        source=source[..begin]+"""
            void PointRay(int2 pixel, out float3 origin, out float3 direction, out bool hit, out float end)
            {
                origin=0; direction=float3(0,0,1); hit=FINITE; end=6;
            }
            """.Replace("FINITE",finite ? "true" : "false")+source[end..];
        begin=source.IndexOf("void PointSuggestionRay(",StringComparison.Ordinal);
        end=source.IndexOf("float3 PointBaselineContribution(",begin,StringComparison.Ordinal);
        source=source[..begin]+"""
            void PointSuggestionRay(float2 uv, out float3 origin, out float3 direction, out float stop)
            {
                bool hit; float end; PointRay(int2(0,0),origin,direction,hit,end);
                stop=STOP;
            }
            """.Replace("STOP",fixture=="depth-edge" ? "uv.x<.5 ? 1 : 12" : "hit ? end : 1e30")+source[end..];
        // Vary only the test's RNG seed to average independent grid realizations.
        File.WriteAllText(common,source.Replace("RasterHash(id.x^RasterHash(id.y+1))",
            "RasterHash(id.x^RasterHash(id.y+1)^(uint)round(65535*Shape.Seed))"));
        using var window=new HiddenWindow();
        int size=fixture is "chromatic" or "varied" or "depth-edge" or "near-core" ? 1024 : 128;
        using var renderer=new Renderer(window.Handle,size,size,32,60,shaderDirectory:shaders.Path,timeProvider:new PointClock(),initialScene:"MandelLightv5");
        PointSet(renderer,"Shape.Enabled",false); PointSet(renderer,"Lights.SampleAirlight",false);
        if (fixture is "chromatic" or "underflow")
        {
            PointSet(renderer,"Air.Density",1f);
            PointSet(renderer,"Air.Scattering.x",.1f); PointSet(renderer,"Air.Scattering.y",.001f); PointSet(renderer,"Air.Scattering.z",.1f);
            PointSet(renderer,"Air.Absorption.x",1f); PointSet(renderer,"Air.Absorption.y",0f); PointSet(renderer,"Air.Absorption.z",1f);
        }
        PointWait(renderer,()=>{});
        float[] reference=PointTexture(renderer,"PointAirlight");
        PointSet(renderer,"Lights.SampleAirlight",true);
        int frames=fixture=="chromatic" ? 64 : fixture=="depth-edge" ? 16 : 1;
        double[] mean=new double[3]; float[] sampled=[];
        for (int frame=0;frame<frames;frame++)
        {
            PointSet(renderer,"Shape.Seed",frame/(float)frames); renderer.Render(false);
            sampled=PointTexture(renderer,"PointAirlight");
            Assert.That(sampled.All(float.IsFinite),Is.True);
            for (int i=0;i<sampled.Length;i+=4) for (int c=0;c<3;c++) mean[c]+=sampled[i+c]/(size*(double)size*frames);
        }
        if (fixture=="singular")
        {
            Assert.That(reference[0],Is.GreaterThan(1e30));
            Assert.That(sampled,Is.EqualTo(reference),"Selected collinear sources retain the unsoftened singular kernel.");
            return;
        }
        double tolerance=fixture=="chromatic" ? .05 : fixture is "identical" or "underflow" ? .001 : .03;
        for (int c=0;c<3;c++)
        {
            TestContext.Progress.WriteLine($"{fixture}, finite={finite}, channel={c}: sampled mean={mean[c]:G7}, exact={reference[c]:G7}");
            Assert.That(mean[c],Is.EqualTo(reference[c]).Within(Math.Max(1e-7,tolerance*reference[c])));
        }
        if (fixture=="varied") Assert.That(sampled,Is.EqualTo(reference).Within(2e-6),"A ray-constant baseline must remove sampling variance.");
        if (fixture=="chromatic") Assert.That(reference[1],Is.GreaterThan(.001),"The missed-by-proxy green light must matter.");
        if (fixture=="underflow") Assert.That(reference[1],Is.GreaterThan(1e-5),"Tiny proposal weights must not erase surviving green radiance.");
    }

    [TestCase("background"), TestCase("mixed"), TestCase("fallback"), TestCase("sloped"), Category("GPU")]
    public void AirlightGridBlendsCoefficientsWithoutInterpolatingIndices(string depth)
    {
        using var shaders=new SceneShaders("MandelLightv5");
        string common=Path.Combine(shaders.Path,"Common","PointRasterAirlight.hlsl");
        string source=File.ReadAllText(common);
        int begin=source.IndexOf("float3 PointAirlightContribution(",StringComparison.Ordinal);
        int end=source.IndexOf("float3 SumPointAirlight(",begin,StringComparison.Ordinal);
        File.WriteAllText(common,source[..begin]+"""
            float3 PointAirlightContribution(PointLight light, float3 origin, float3 direction, float stop,
                float3 scatter, float3 extinction, float4 layout) { return light.Intensity; }
            """+source[end..]);
        if (depth!="background")
        {
            source=File.ReadAllText(common);
            begin=source.IndexOf("void PointRay(",StringComparison.Ordinal);
            end=source.IndexOf("float3 PointAirlightContribution(",begin,StringComparison.Ordinal);
            source=source[..begin]+"""
                void PointRay(int2 pixel, out float3 origin, out float3 direction, out bool hit, out float end)
                { origin=0; direction=float3(0,0,1); hit=true; end=6; }
                """+source[end..];
            begin=source.IndexOf("void PointSuggestionRay(",StringComparison.Ordinal);
            end=source.IndexOf("float3 PointBaselineContribution(",begin,StringComparison.Ordinal);
            source=source[..begin]+"""
                void PointSuggestionRay(float2 uv, out float3 origin, out float3 direction, out float stop)
                { origin=0; direction=normalize(float3(uv-.5,1)); stop=STOP; }
                """.Replace("STOP",depth=="mixed" ? "uv.x<.5 ? 6 : 1e30" : depth=="fallback" ? "12" : "6*(1+.04*uv.x)")+source[end..];
            File.WriteAllText(common,source);
        }
        source=File.ReadAllText(common);
        begin=source.IndexOf("float3 PointBaselineContribution(",StringComparison.Ordinal);
        end=source.IndexOf("[numthreads(8,8,1)]",begin,StringComparison.Ordinal);
        File.WriteAllText(common,source[..begin]+"""
            float3 PointBaselineContribution(PointLight light, float3 origin, float3 direction, float stop,
                float3 scatter, float3 extinction, float4 layout) { return light.Intensity*(1.5+.25*direction.x); }
            """+source[end..]);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,97,55,32,60,shaderDirectory:shaders.Path,timeProvider:new PointClock(),initialScene:"MandelLightv5");
        PointSet(renderer,"Shape.Enabled",false);
        foreach (var (width,height) in new[] {(97,55),(1,1),(128,72)})
        {
            renderer.Resize(width,height); PointFeed(renderer,0); renderer.Render(false);
            var grid=PointSuggestions(renderer); var lights=PointRecords(renderer);
            float[] baseline=PointBuffer(renderer,"PointBaseline"),perLight=PointBuffer(renderer,"PointLightBaselines");
            int nx=(width+15)/16+1,ny=(height+15)/16+1;
            Assert.That(grid.Length,Is.EqualTo(nx*ny*2));
            Assert.That(grid.All(s=>s.Index<lights.Length && float.IsFinite(s.Weight) && s.Weight>0),Is.True);
            float[] image=PointTexture(renderer,"PointAirlight");
            double error=0,maximum=0; int rejected=0,fallback=0;
            for (int y=0;y<height;y++) for (int x=0;x<width;x++)
            {
                double gx=(x+.5)/width*(nx-1),gy=(y+.5)/height*(ny-1);
                int cx=Math.Min((int)gx,nx-2),cy=Math.Min((int)gy,ny-2);
                double fx=gx-cx,fy=gy-cy;
                int[] nodes=[cy*nx+cx,cy*nx+cx+1,(cy+1)*nx+cx,(cy+1)*nx+cx+1];
                double[] a=[(1-fx)*(1-fy),fx*(1-fy),(1-fx)*fy,fx*fy];
                double stop=depth=="background" ? 1e30 : 6;
                double[] b=Enumerable.Range(0,4).Select(k=>a[k]*Compatible(stop,baseline[nodes[k]*4+3])).ToArray();
                double total=b.Sum();
                if (total<.999999) ++rejected;
                if (total>1e-6) { for (int k=0;k<4;k++) b[k]/=total; a=(double[])b.Clone(); }
                else { Array.Fill(b,0); ++fallback; }
                for (int c=0;c<3;c++)
                {
                    double expected=Enumerable.Range(0,4).Sum(k=>b[k]*baseline[nodes[k]*4+c]);
                    for (int j=0;j<2;j++) for (int i=0;i<2;i++) for (int m=0;m<2;m++)
                    {
                        var s=grid[((cy+j)*nx+cx+i)*2+m];
                        var intensity=lights[s.Index].Intensity;
                        double approximate=Enumerable.Range(0,4).Sum(k=>b[k]*perLight[(nodes[k]*512+s.Index)*3+c]);
                        expected+=a[j*2+i]*s.Weight*((c==0 ? intensity.X : c==1 ? intensity.Y : intensity.Z)-approximate);
                    }
                    error=Math.Max(error,Math.Abs(image[(y*width+x)*4+c]-expected)); maximum=Math.Max(maximum,Math.Abs(expected));
                }
            }
            Assert.That(error,Is.LessThan(maximum*3e-6),"Every pixel must match baseline plus signed residuals, including cell boundaries.");
            if (depth!="background") Assert.That(rejected,Is.GreaterThan(0));
            if (depth=="fallback") Assert.That(fallback,Is.EqualTo(width*height));
            PointSet(renderer,"Lights.Enabled",false); renderer.Render(false);
            Assert.That(PointSuggestions(renderer).All(s=>s.Weight==0),Is.True,"Zero count must clear stale suggestions.");
            Assert.That(PointTexture(renderer,"PointAirlight").Where((_,i)=>i%4<3).All(x=>x==0),Is.True);
            PointSet(renderer,"Lights.Enabled",true);
        }
        static double Compatible(double stop, double node)
        {
            if (stop>=1e30 || node>=1e30) return stop>=1e30 && node>=1e30 ? 1 : 0;
            double w=Math.Clamp(1-Math.Abs(stop-node)/Math.Max(.05*Math.Max(stop,node),1e-6),0,1);
            return w*w;
        }
    }

    [Test, Category("GPU")]
    public void AirlightSamplingToggleRestoresTheExactImageAndDoesNotChangeSurfaces()
    {
        using var shaders=new SceneShaders("MandelLightv5"); using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,96,54,32,60,shaderDirectory:shaders.Path,timeProvider:new PointClock(),initialScene:"MandelLightv5");
        PointSet(renderer,"Lights.SampleAirlight",false); PointWait(renderer,()=>PointFeed(renderer,0));
        float[] surface=PointTexture(renderer,"PointSurface"),exactAir=PointTexture(renderer,"PointAirlight");
        byte[] exactImage=renderer.ReadPixels(false);
        PointSet(renderer,"Lights.SampleAirlight",true); PointFeed(renderer,0); renderer.Render(false);
        Assert.That(PointTexture(renderer,"PointSurface"),Is.EqualTo(surface));
        Assert.That(PointTexture(renderer,"PointAirlight"),Is.Not.EqualTo(exactAir));
        byte[] sampledImage=renderer.ReadPixels(false);
        PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(sampledImage),"A stationary frame must retain its sampling pattern.");
        PointSet(renderer,"Lights.SampleAirlight",false); PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(exactImage));
        PointSet(renderer,"Lights.SampleAirlight",true); PointSet(renderer,"Air.Density",0f);
        PointFeed(renderer,0); renderer.Render(false); byte[] vacuum=renderer.ReadPixels(false);
        PointSet(renderer,"Lights.SampleAirlight",false); PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(vacuum));
    }

    static (uint Index,float Weight)[] PointSuggestions(Renderer renderer)
    {
        var device=PointField<ID3D11Device>(renderer,"device"); var context=PointField<ID3D11DeviceContext>(renderer,"context");
        var views=PointField<System.Collections.Generic.Dictionary<string,ID3D11ShaderResourceView>>(renderer,"namedTextures");
        using var resource=views["PointLightSuggestions"].Resource; using var source=resource.QueryInterface<ID3D11Buffer>();
        using var staging=device.CreateBuffer(new BufferDescription {ByteWidth=source.Description.ByteWidth,Usage=ResourceUsage.Staging,CPUAccessFlags=CpuAccessFlags.Read});
        context.CopyResource(staging,source);
        var map=context.Map(staging,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            byte[] bytes=new byte[source.Description.ByteWidth]; Marshal.Copy(map.DataPointer,bytes,0,bytes.Length);
            return Enumerable.Range(0,bytes.Length/8).Select(i=>(BitConverter.ToUInt32(bytes,8*i),BitConverter.ToSingle(bytes,8*i+4))).ToArray();
        }
        finally {context.Unmap(staging,0);}
    }

    static float[] PointBuffer(Renderer renderer, string name)
    {
        var device=PointField<ID3D11Device>(renderer,"device"); var context=PointField<ID3D11DeviceContext>(renderer,"context");
        var views=PointField<System.Collections.Generic.Dictionary<string,ID3D11ShaderResourceView>>(renderer,"namedTextures");
        using var resource=views[name].Resource; using var source=resource.QueryInterface<ID3D11Buffer>();
        using var staging=device.CreateBuffer(new BufferDescription {ByteWidth=source.Description.ByteWidth,Usage=ResourceUsage.Staging,CPUAccessFlags=CpuAccessFlags.Read});
        context.CopyResource(staging,source);
        var map=context.Map(staging,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            float[] values=new float[source.Description.ByteWidth/4]; Marshal.Copy(map.DataPointer,values,0,values.Length); return values;
        }
        finally {context.Unmap(staging,0);}
    }

    static float[] PointTexture(Renderer renderer, string name)
    {
        var device=PointField<ID3D11Device>(renderer,"device"); var context=PointField<ID3D11DeviceContext>(renderer,"context");
        var views=PointField<System.Collections.Generic.Dictionary<string,ID3D11ShaderResourceView>>(renderer,"namedTextures");
        using var resource=views[name].Resource; using var source=resource.QueryInterface<ID3D11Texture2D>();
        var desc=source.Description; desc.Usage=ResourceUsage.Staging; desc.BindFlags=BindFlags.None; desc.CPUAccessFlags=CpuAccessFlags.Read;
        using var staging=device.CreateTexture2D(desc); context.CopyResource(staging,source);
        var map=context.Map(staging,0,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width=(int)desc.Width,height=(int)desc.Height; var values=new float[width*height*4];
            for (int y=0;y<height;y++) Marshal.Copy(map.DataPointer+y*(int)map.RowPitch,values,y*width*4,width*4);
            return values;
        }
        finally {context.Unmap(staging,0);}
    }
}

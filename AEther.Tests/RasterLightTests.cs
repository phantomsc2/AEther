using System;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    const string RasterProbe = """
        #include "../Common/Projection.hlsl"
        struct MediumSettings { float Density; float3 Scattering; float3 Absorption; };

        cbuffer Parameters
        {

            MediumSettings Medium = { 0, float3(1,1,1), float3(0,0,0) };
        };
        ProjectionCamera SceneCamera()
        { return MakeProjectionCamera(0,float3(0,0,1),float3(1,0,0),float3(0,1,0)); }
        #include "../Common/RasterGeometry.hlsl"
        #include "../Common/RasterLight.hlsl"
        float3 AirScatter() { return .3*Medium.Density*Medium.Density*Medium.Scattering; }
        float3 AirExtinction() { return .3*Medium.Density*Medium.Density*(Medium.Scattering+Medium.Absorption); }
        GeometryVertex EvaluateGeometry(uint vertex, uint instance)
        {
            float2 c[6]={float2(-1,-1),float2(-1,1),float2(1,-1),float2(1,-1),float2(-1,1),float2(1,1)};
            GeometryVertex v; v.World=float3(100*c[vertex],8); v.Normal=float3(0,0,-1); v.Albedo=.5; return v;
        }
        PointLight EvaluateLight(uint index)
        { PointLight l; l.Position=0; l.Intensity=100; return l; }
        float4 PS(PSin input) : SV_Target
        {
            float3 surface=RasterSurface.Load(int3(input.Position.xy,0)).rgb;
            return LinearOutput(surface*exp(-AirExtinction()*8)+RasterAirlight.Load(int3(input.Position.xy,0)).rgb);
        }
        """;

    [Test, Category("GPU")]
    public void RasterLightUsesLambertianSurfaceAndShaderDefinedDrawCounts()
    {
        using var shaders = new SceneShaders("Spectrum");
        string file = Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl");
        File.WriteAllText(file,RasterProbe);
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle,64,36,8,60,shaderDirectory:shaders.Path,initialScene:"Spectrum",sceneDefinitions:[new SceneDefinition("Spectrum",(d,c,s)=>[new RasterEffect("Raster",d,c,s,"Spectrum.hlsl",new RasterOptions(PointLights:1,Vertices:6,Instances:1,LookupWidth:513,LookupHeight:1025))])]);
        bool integrated=false;
        renderer.TransportPassMarker=(name,begin)=> { if(name=="Raster airlight" && begin) integrated=true; };
        renderer.Render(false);
        Assert.That(integrated,Is.True,"Airlight runs without any View settings.");
        var pixels=renderer.ReadPixels(false);
        foreach (int x in new[] { 8,32,56 })
        {
            const int y=18;
            double dx=8*(2*(x+.5)/64-1)*(64.0/36)/2, dy=8*(1-2*(y+.5)/36)/2;
            double r=Math.Sqrt(dx*dx+dy*dy+64), light=.5*100*8/(Math.PI*r*r*r);
            Assert.That(pixels[4*(y*64+x)],Is.EqualTo(255*Srgb(1-Math.Exp(-light))).Within(1));
        }
        foreach (var counts in new[] { (0u,1u), (6u,0u) })
        {
            SetRasterOptions(renderer, RasterSettings(renderer) with { Vertices=counts.Item1, Instances=counts.Item2 });
            Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);
            renderer.ActiveShader!.Effects[0].Parameters!.Reset();
            renderer.Render(false);
            Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        }
        SetRasterOptions(renderer, RasterSettings(renderer) with { Vertices=6, Instances=1 });
        File.WriteAllText(file,RasterProbe);
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);
        renderer.ActiveShader!.Effects[0].Parameters!.Reset();
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(pixels));
        File.WriteAllText(file,RasterProbe.Replace("return .3*Medium.Density*Medium.Density*(Medium.Scattering+Medium.Absorption);","return MissingMedium;"));
        Assert.That(renderer.ReloadShaders(),Is.False,"Invalid medium HLSL must fail during reload.");
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(pixels));

        // Medium callbacks can be constant; allocation settings remain in C#.
        string constant=System.Text.RegularExpressions.Regex.Replace(RasterProbe,
            @"struct MediumSettings[^\n]*\n| +MediumSettings Medium[^\n]*\n", "");
        constant=constant.Replace("return .3*Medium.Density*Medium.Density*Medium.Scattering;","return 0;")
            .Replace("return .3*Medium.Density*Medium.Density*(Medium.Scattering+Medium.Absorption);","return 0;");
        File.WriteAllText(file,constant);
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);
        Assert.That(renderer.ActiveShader!.Effects[0].Parameters, Is.Null);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(pixels));
    }
}

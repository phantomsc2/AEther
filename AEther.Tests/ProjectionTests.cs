using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    const string ProjectionProbeCamera = """
        #include "../Common/Projection.hlsl"
        ProjectionCamera ProbeCamera()
        {
            float3 forward=normalize(float3(1,.5,2));
            float3 right=normalize(cross(float3(0,1,0),forward)),up=cross(forward,right);
            ProjectionCamera camera=MakeProjectionCamera(float3(3,-4,6),forward,right,up);
            return camera;
        }
        """;

    [TestCase(160,90), TestCase(90,160), TestCase(96,96), Category("GPU")]
    public void PerspectiveDepthAndPixelPositionsRoundTripOnGpu(int width, int height)
    {
        using var shaders=new SceneShaders("Spectrum");
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"),ProjectionProbeCamera+"""
            float4 PS(PSin input) : SV_Target
            {
                ProjectionCamera camera=ProbeCamera();
                if (input.UV.y>.5) { camera.Near=.125; camera.Far=96; camera.Focal=1.4; }
                float z=exp(lerp(log(camera.Near),log(camera.Far),input.UV.x));
                float3 direction=ProjectionDirection(camera,input.UV);
                float3 world=ProjectionPosition(camera,direction,z);
                float4 clip=ProjectionClip(camera,world);
                float depth=clip.z/clip.w;
                float2 uv=.5+.5*clip.xy/clip.w*float2(1,-1);
                float3 reconstructed=ProjectionReconstruct(camera,input.UV,depth);
                float relativeDepth=abs(ProjectionLinearDepth(camera,depth)-z)/z;
                bool endpoints=abs(ProjectionDepth(camera.Near,camera.Near,camera.Far))<1e-6
                    && abs(ProjectionDepth(camera.Far,camera.Near,camera.Far)-1)<1e-6;
                bool position=length(reconstructed-world)<.001*max(1,length(world-camera.Origin));
                float t=ProjectionRayDistance(camera,direction,z);
                bool rayDistance=abs(length(world-camera.Origin)-t)<.001*max(1,t);
                // The view-space primitive supports an explicit XY projection scale.
                float4 scaledClip=ProjectionViewClip(float3(.3,-.4,2),float2(1.7,1.7),camera.Near,camera.Far);
                bool scaledXY=all(abs(scaledClip.xy/scaledClip.w-float2(.255,-.34))<1e-6);
                bool scaledZ=abs(ProjectionLinearDepth(scaledClip.z/scaledClip.w,camera.Near,camera.Far)-2)<.001;
                return float4(!endpoints || relativeDepth>.001,
                    any(abs(uv-input.UV)>.0001),!position || !rayDistance || !scaledXY || !scaledZ,1);
            }
            """);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,width,height,32,60,shaderDirectory:shaders.Path,initialScene:"Spectrum");
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
    }

    [TestCase(160,90), TestCase(90,160), TestCase(96,96), Category("GPU")]
    public void PerspectiveRaysMatchIndependentVerticalFovAndPlaneIntersection(int width, int height)
    {
        using var shaders=new SceneShaders("Spectrum");
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"),ProjectionProbeCamera+"""
            float4 PS(PSin input) : SV_Target
            {
                ProjectionCamera camera=ProbeCamera();
                float3 direction=ProjectionDirection(camera,input.UV);
                return float4(.5+.5*direction,1);
            }
            """);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,width,height,32,60,shaderDirectory:shaders.Path,initialScene:"Spectrum");
        renderer.Render(false); byte[] pixels=renderer.ReadPixels(false);
        Vector3 forward=Vector3.Normalize(new(1,.5f,2)),right=Vector3.Normalize(Vector3.Cross(Vector3.UnitY,forward)),up=Vector3.Cross(forward,right);
        for (int y=0;y<height;y+=7) for (int x=0;x<width;x+=7)
        {
            Vector3 direction=Vector3.Normalize(2*forward+(2*(x+.5f)/width-1)*width/height*right+(1-2*(y+.5f)/height)*up);
            for (int c=0;c<3;c++) Assert.That(pixels[4*(y*width+x)+c],Is.EqualTo(255*(.5+.5*direction[c])).Within(1.5));
        }
        // Exercise clipping and the miss contract independently of camera orientation.
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"),ProjectionProbeCamera+"""
            float4 PS(PSin input) : SV_Target
            {
                ProjectionCamera c=MakeProjectionCamera(0,float3(0,0,1),float3(1,0,0),float3(0,1,0));
                float3 d=normalize(float3(1,2,3));
                float t=ProjectionPlane(c,d,float3(0,0,1),6);
                float a=0,b=1000;
                bool clipped=ProjectionClipInterval(c,d,a,b);
                bool valid=abs(t-6/d.z)<1e-5 && ProjectionPlane(c,d,float3(0,0,1),-1)<0
                    && ProjectionPlane(c,d,float3(0,0,1),.01)<0 && ProjectionPlane(c,d,float3(0,0,1),200)<0
                    && ProjectionPlane(c,float3(1,0,0),float3(0,0,1),6)<0
                    && clipped && abs(a-c.Near/d.z)<1e-5 && abs(b-c.Far/d.z)<.001;
                return float4((!valid).xxx,1);
            }
            """);
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
    }

    [TestCase(160,90), TestCase(90,160), TestCase(96,96), Category("GPU")]
    public void RasterDepthReconstructsTheSameCameraPlane(int width, int height)
    {
        using var shaders=new SceneShaders("MandelLightv5");
        string file=Path.Combine(shaders.Path,"Scenes","MandelLightv5.hlsl"),source=File.ReadAllText(file);
        int begin=source.IndexOf("uint2 GeometryCount()",StringComparison.Ordinal),end=source.IndexOf("static const float SceneLightGain",begin,StringComparison.Ordinal);
        source=source[..begin]+"""
            uint2 GeometryCount() { return uint2(6,1); }
            GeometryVertex EvaluateGeometry(uint vertex, uint instance)
            {
                float2 corners[6]={float2(-1,-1),float2(-1,1),float2(1,-1),float2(1,-1),float2(-1,1),float2(1,1)};
                ProjectionCamera c=SceneCamera(); GeometryVertex v;
                v.World=c.Origin+8*c.Forward+100*(corners[vertex].x*c.Right+corners[vertex].y*c.Up);
                v.Normal=-c.Forward; v.Albedo=0; return v;
            }
            """+source[end..];
        begin=source.IndexOf("float4 PS(",StringComparison.Ordinal);
        source=source[..begin]+"""
            float4 PS(PSin input) : SV_Target
            {
                int2 pixel=(int2)input.Position.xy;
                float depth=RasterDepth.Load(int3(pixel,0));
                ProjectionCamera c=SceneCamera();
                float3 world=ProjectionReconstruct(c,input.UV,depth);
                float error=abs(ProjectionView(c,world).z-8);
                return float4((depth>=1 || error>.003).xxx+RasterSurface.Load(int3(pixel,0)).rgb,1);
            }
            """;
        File.WriteAllText(file,source);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,width,height,32,60,shaderDirectory:shaders.Path,initialScene:"MandelLightv5");
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
    }

    [TestCase("MandelLightv5"), Category("GPU")]
    public void ProjectionFarPlaneDoesNotTruncateInfiniteAirlight(string scene)
    {
        using var shaders=new SceneShaders(scene);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,96,54,32,60,shaderDirectory:shaders.Path,timeProvider:new PointClock(),initialScene:scene);
        PointSet(renderer,"Shape.Enabled",false);
        PointRender(renderer,()=>PointFeed(renderer,0));
        byte[] original=renderer.ReadPixels(false);
        Assert.That(original.Where((_,i)=>i%4!=3).Any(x=>x>0),Is.True);
        string file=Path.Combine(shaders.Path,"Scenes",scene+".hlsl");
        string source=File.ReadAllText(file).Replace("return MakeProjectionCamera(origin,forward,right,up);",
            "ProjectionCamera probe=MakeProjectionCamera(origin,forward,right,up); probe.Near=.5; probe.Far=1; return probe;");
        File.WriteAllText(file,source);
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);
        PointFeed(renderer,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false),Is.EqualTo(original));
    }

    [TestCase("FluidFlight"), TestCase("PlaneFlight"), Category("GPU")]
    public void FluidFlightIntersectionsProjectBackToTheInjectionPixel(string scene)
    {
        using var shaders=new SceneShaders(scene);
        string common=Path.Combine(shaders.Path,"Common","FluidFlight.hlsl");
        File.WriteAllText(common,File.ReadAllText(common).Replace("float4 PS(PSin input)","float4 FlightDisplayPS(PSin input)"));
        File.AppendAllText(Path.Combine(shaders.Path,"Scenes",scene+".hlsl"),"""

            float4 PS(PSin input) : SV_Target
            {
                float4 hit=FlightSurfaceHit((2*input.UV-1)*float2(AspectRatio,1));
                if (hit.w<=0) return float4(FluidMaterial0.Load(int3(0,0,0)).rgb,1);
                ProjectionCamera c=FlightView();
                float4 clip=ProjectionClip(c,hit.xyz);
                float2 uv=.5+.5*clip.xy/clip.w*float2(1,-1);
            #ifdef FLIGHT_PLANE_PROJECTION
                bool surface=abs(hit.y)<.001;
            #else
                bool surface=abs(length(hit.xy)-(.8+.6*Flight.SourceDistance))<.001;
            #endif
                bool valid=surface && all(abs(uv-input.UV)<.0001) && abs(clip.w-hit.w)<.001;
                return float4(!valid,.25,0,1)+float4(FluidMaterial0.Load(int3(0,0,0)).rgb,0);
            }
            """);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,96,54,32,60,shaderDirectory:shaders.Path,timeProvider:new PointClock(),initialScene:scene);
        PointSet(renderer,"Flight.Steering.x",.65f); PointSet(renderer,"Flight.Steering.y",.7f);
        PointSet(renderer,"Flight.Roll",.6f); renderer.Render(false);
        var pixels=renderer.ReadPixels(false);
        Assert.That(pixels.Where((_,i)=>i%4==0),Is.All.Zero);
        Assert.That(pixels.Where((_,i)=>i%4==1).Any(x=>x>0),Is.True,"The test must intersect the world surface.");
    }
}

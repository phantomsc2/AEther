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

    [Test, Category("GPU")]
    public void FroxelRasterDrawsMovingOctahedraAtCanvasResolution()
    {
        using var shaders=new SceneShaders("Froxel1");
        using var window=new HiddenWindow();
        var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,1280,720,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:shaders.Scene);
        var effect=(FroxelEffect)renderer.ActiveShader!.Effects[0];
        Assert.That(effect.Options.Instances,Is.EqualTo(1024));
        PointSet(renderer,"Medium.Density",0);
        PointSet(renderer,"Lights.Speed",0);
        effect.Options=effect.Options with { Instances=0 };
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        effect.Options=effect.Options with { Instances=1024 };
        var markers=new System.Collections.Generic.List<string>();
        renderer.TransportPassMarker=(name,start)=> { if(start) markers.Add(name); };
        renderer.Render(false); var first=renderer.ReadPixels(false);
        Assert.That(first.Where((_,i)=>i%4!=3).Count(v=>v>20),Is.GreaterThan(1000),"Surfaces must be visible even with zero fog.");
        Assert.That(markers,Does.Contain("Froxel raster"));
        clock.Frame=15; renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).SequenceEqual(first),Is.False,"Geometry moves independently of light and medium animation.");
        PointSet(renderer,"Medium.Density",.159f); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).SequenceEqual(first),Is.False);
        renderer.Resize(1281,721); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Length,Is.EqualTo(1281*721*4));
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);
        renderer.Render(false);
        Assert.That(((FroxelEffect)renderer.ActiveShader!.Effects[0]).Options.Instances,Is.EqualTo(1024));
    }

    [Test, Category("GPU")]
    public void FroxelLightsBlinkWithOffsetSineCycles()
    {
        using var shaders=new SceneShaders("Froxel1");
        using var window=new HiddenWindow();
        var clock=new FroxelClock();
        using var renderer=new Renderer(window.Handle,64,36,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:shaders.Scene);
        var effect=(FroxelEffect)renderer.ActiveShader!.Effects[0];
        float[] Intensities()
        {
            renderer.Render(false);
            var device=PointField<ID3D11Device>(renderer,"device");
            var context=PointField<ID3D11DeviceContext>(renderer,"context");
            var view=PointField<ID3D11ShaderResourceView>(PointField<object>(effect,"lights"),"View");
            using var resource=view.Resource;
            using var buffer=resource.QueryInterface<ID3D11Buffer>();
            var desc=buffer.Description; desc.Usage=ResourceUsage.Staging; desc.BindFlags=BindFlags.None; desc.CPUAccessFlags=CpuAccessFlags.Read;
            using var staging=device.CreateBuffer(desc); context.CopyResource(staging,buffer);
            var map=context.Map(staging,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
            try
            {
                var values=new float[24]; Marshal.Copy(map.DataPointer,values,0,24);
                return Enumerable.Range(0,4).Select(i=>values[i*6+3]+values[i*6+4]+values[i*6+5]).ToArray();
            }
            finally { context.Unmap(staging,0); }
        }
        clock.Frame=79; var bright=Intensities(); // Light zero near its sine maximum.
        clock.Frame=288; var dark=Intensities(); // Same light near its sine minimum.
        Assert.That(bright[0],Is.GreaterThan(100));
        Assert.That(dark[0],Is.LessThan(bright[0]*.0001));
        Assert.That(dark.Skip(1).Any(v=>v>100),Is.True,"Other lights must retain their phase offsets.");
    }

    [TestCase(1280,720,160,90), TestCase(1920,1080,240,135), TestCase(1281,721,161,91), TestCase(1,1,1,1)]
    public void FroxelGridFollowsCanvas(int width, int height, int x, int y)
    {
        Assert.That(new FroxelOptions().Resolve(width,height), Is.EqualTo((x,y,64)));
        Assert.That(new FroxelOptions(128,64).Resolve(width,height), Is.EqualTo((x,y,128)));
        foreach (var bad in new[] { new FroxelOptions(7), new FroxelOptions(257), new FroxelOptions(64,-1), new FroxelOptions(64,65) })
            Assert.Throws<InvalidOperationException>(() => bad.Resolve(width,height));
    }

    [Test, Category("GPU")]
    public void FroxelSceneAnimatesWithoutAudioAndOwnsItsStorage()
    {
        using var shaders = new SceneShaders("Froxel1");
        using var window = new HiddenWindow();
        var clock = new FroxelClock();
        using var renderer = new Renderer(window.Handle,128,72,8,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:shaders.Scene);
        var effect = (FroxelEffect)renderer.ActiveShader!.Effects[0];
        renderer.Render(false);
        effect.Options=effect.Options with { Instances=0 }; renderer.Render(false);
        var original = renderer.ReadPixels(false);
        Assert.That(original.Where((_,i) => i%4!=3).Any(v => v>20), Is.True);
        renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original), "No frame-index jitter or history.");
        var storage = effect.IntegratedTexture;
        effect.Options = effect.Options with { PointLights = 16 }; renderer.Render(false);
        Assert.That(effect.IntegratedTexture, Is.SameAs(storage));
        effect.Options = effect.Options with { PointLights = 4 }; renderer.Render(false);
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(original));
        clock.Frame = 120; renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).SequenceEqual(original), Is.False);
        // Each animated subsystem must change the image independently.
        PointSet(renderer,"Medium.Speed",0); renderer.Render(false); var a=renderer.ReadPixels(false);
        clock.Frame=180; renderer.Render(false); Assert.That(renderer.ReadPixels(false).SequenceEqual(a),Is.False,"Light animation");
        PointSet(renderer,"Lights.Speed",0); PointSet(renderer,"Medium.Speed",.3f);
        renderer.Render(false); a=renderer.ReadPixels(false);
        clock.Frame=360; renderer.Render(false); Assert.That(renderer.ReadPixels(false).SequenceEqual(a),Is.False,"Medium animation");
        PointSet(renderer,"Medium.Speed",0); renderer.Render(false); a=renderer.ReadPixels(false);
        clock.Frame=420; renderer.Render(false); Assert.That(renderer.ReadPixels(false).SequenceEqual(a),Is.True);
        foreach (var control in new[] { "Camera.Distance", "Camera.Yaw", "Camera.Pitch" })
        {
            PointSet(renderer,control,.8f); renderer.Render(false);
            Assert.That(renderer.ReadPixels(false).SequenceEqual(a),Is.False,control);
        }
        PointSet(renderer,"Medium.Density",0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        PointSet(renderer,"Medium.Density",.5f); effect.Options=new(32,0); renderer.Render(false);
        Assert.That(renderer.ReadPixels(false).Where((_,i)=>i%4!=3),Is.All.Zero);
        Assert.That(effect.Dimensions,Is.EqualTo((16,9,32)));
        Assert.That(effect.IntegratedTexture,Is.Not.SameAs(storage));
        renderer.Resize(137,81); renderer.Render(false);
        Assert.That(effect.Dimensions,Is.EqualTo((18,11,32)));
        effect.Options=new(7,4); renderer.Render(false);
        Assert.That(effect.Options,Is.EqualTo(new FroxelOptions(32,0)));
        Assert.That(renderer.ShaderError,Does.Contain("DepthSlices"));
        Assert.That(renderer.ReloadShaders(),Is.True,renderer.ShaderError);
        effect=(FroxelEffect)renderer.ActiveShader!.Effects[0];
        Assert.That(effect.Options,Is.EqualTo(new FroxelOptions(32,0)));
        renderer.Render(false);
        string file=Path.Combine(shaders.Path,"Scenes","Froxel1.hlsl");
        File.AppendAllText(file,"\nINVALID HLSL");
        Assert.That(renderer.ReloadShaders(),Is.False);
        Assert.That(renderer.ActiveShader.Effects[0],Is.SameAs(effect));
        Assert.DoesNotThrow(()=>renderer.Render(false));
        renderer.SelectScene(null);
        Assert.That(effect.Dimensions,Is.EqualTo((0,0,0)));
        renderer.SelectScene(shaders.Scene); renderer.Render(false);
        Assert.That(effect.Dimensions,Is.EqualTo((18,11,32)));
        var values=ReadFroxelVolume(renderer,effect.IntegratedTexture);
        Assert.That(values.All(v=>float.IsFinite(v) && v>=0),Is.True);
        for(int z=1;z<32;z++) for(int pixel=0;pixel<18*11;pixel++)
            Assert.That(values[4*(z*18*11+pixel)+3],Is.LessThanOrEqualTo(values[4*((z-1)*18*11+pixel)+3]));
        PointSet(renderer,"Medium.Density",1); PointSet(renderer,"Medium.Anisotropy",1);
        PointSet(renderer,"Lights.Intensity",1); PointSet(renderer,"Lights.Radius",0);
        PointSet(renderer,"Camera.Distance",.1f); effect.Options=new(64,64); renderer.Render(false);
        Assert.That(ReadFroxelVolume(renderer,effect.IntegratedTexture).All(v=>float.IsFinite(v) && v>=0),Is.True,
            "Extreme brightness must saturate float16 storage without producing infinities.");
    }

    [TestCase(8), TestCase(64), TestCase(256), Category("GPU")]
    public void FroxelIntegrationMatchesHomogeneousAnalyticSolution(int slices)
    {
        using var shaders=new SceneShaders("Froxel1");
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Froxel1.hlsl"),"""
            #define InjectFroxels UnusedInjection
            #include "../Common/Froxel.hlsl"
            #undef InjectFroxels
            ProjectionCamera FroxelCamera() { return MakeProjectionCamera(0,float3(0,0,1),float3(1,0,0),float3(0,1,0)); }
            FroxelLight EvaluateFroxelLight(uint i) { FroxelLight l; l.Position=0; l.Intensity=0; return l; }
            FroxelMedium EvaluateFroxelMedium(float3 p) { FroxelMedium m; m.Scattering=0; m.Extinction=0; m.Anisotropy=0; return m; }
            float FroxelLightTransmittance(float3 p,float3 l) { return 1; }
            float FroxelSourceRadius() { return 1; }
            [numthreads(8,8,1)]
            void InjectFroxels(uint3 id:SV_DispatchThreadID) {
                if(id.x>=FroxelWidth || id.y>=FroxelHeight || id.z>=FroxelDepth) return;
                float kt=id.x%4==0 ? 0 : id.x%4==1 ? 1e-6 : id.x%4==2 ? .1 : 10;
                FroxelSourceOutput[id]=float4(.125,.25,.5,kt);
            }
            float4 PS(PSin input):SV_Target { return LinearOutput(FroxelRadiance(input.UV)); }
            """);
        using var window=new HiddenWindow();
        using var renderer=new Renderer(window.Handle,64,64,8,60,shaderDirectory:shaders.Path,initialScene:shaders.Scene);
        var effect=(FroxelEffect)renderer.ActiveShader!.Effects[0]; effect.Options=new(slices,0,0); renderer.Render(false);
        var values=ReadFroxelVolume(renderer,effect.IntegratedTexture);
        for(int z=0;z<slices;z++) for(int y=0;y<8;y++) for(int x=0;x<8;x++)
        {
            double depth=Math.Pow(65,(z+1.0)/slices)-1;
            double sx=2*(x+.5)/8-1, sy=1-2*(y+.5)/8;
            double distance=depth*Math.Sqrt(1+(sx*sx+sy*sy)/4);
            double kt=(x%4) switch { 0=>0,1=>1e-6,2=>(double)(Half).1f,_=>10 };
            double t=Math.Exp(-kt*distance), weight=kt==0 ? distance : (1-t)/kt;
            int at=4*((z*8+y)*8+x);
            for(int c=0;c<3;c++) Assert.That(values[at+c],Is.EqualTo(weight*.125*(1<<c)).Within(Math.Max(.001,weight*.002)), $"{x},{y},{z},channel {c}");
            Assert.That(values[at+3],Is.EqualTo(t).Within(.001));
        }
    }

    static float[] ReadFroxelVolume(Renderer renderer, ID3D11Texture3D texture)
    {
        var device=PointField<ID3D11Device>(renderer,"device");
        var context=PointField<ID3D11DeviceContext>(renderer,"context");
        var desc=texture.Description; desc.Usage=ResourceUsage.Staging; desc.BindFlags=BindFlags.None; desc.CPUAccessFlags=CpuAccessFlags.Read;
        using var staging=device.CreateTexture3D(desc); context.CopyResource(staging,texture);
        var map=context.Map(staging,0,MapMode.Read,global::Vortice.Direct3D11.MapFlags.None);
        try
        {
            int width=(int)desc.Width,height=(int)desc.Height,depth=(int)desc.Depth;
            var bytes=new byte[width*height*depth*8];
            for(int z=0;z<depth;z++) for(int y=0;y<height;y++)
                Marshal.Copy(map.DataPointer+z*(int)map.DepthPitch+y*(int)map.RowPitch,bytes,8*width*(z*height+y),8*width);
            return MemoryMarshal.Cast<byte,Half>(bytes).ToArray().Select(v=>(float)v).ToArray();
        }
        finally { context.Unmap(staging,0); }
    }
    sealed class FroxelClock : TimeProvider
    {
        public long Frame;
        public override long TimestampFrequency => 60;
        public override long GetTimestamp() => Frame;
    }
}

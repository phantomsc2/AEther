using System;
using System.IO;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;
public partial class ShaderRenderingTests
{
    [Test, Category("GPU")]
    public void PlanetCameraBufferMatchesCalculatedCameraAt720p()
    {
        using var cached=new SceneShaders("Planet");
        using var calculated=new SceneShaders("Planet");
        string path=Path.Combine(calculated.Path,"Scenes","Planet.hlsl");
        string source=File.ReadAllText(path);
        source=source.Replace("// Separate disk", """
            #define PlanetCamera PlanetCalculateCamera
            #define PlanetObserver PlanetCalculateObserver
            #define PlanetLimbAngle PlanetCalculateLimbAngle
            #define PlanetAngularScale PlanetCalculateAngularScale
            #define PlanetDisplayCamera PlanetCalculateDisplayCamera
            // Separate disk
            """);
        File.WriteAllText(path,source);
        using var wa=new HiddenWindow(); using var wb=new HiddenWindow();
        using var a=new Renderer(wa.Handle,1280,720,8,60,shaderDirectory:cached.Path,initialScene:cached.Scene);
        using var b=new Renderer(wb.Handle,1280,720,8,60,shaderDirectory:calculated.Path,initialScene:calculated.Scene);
        foreach(var setting in new[]{("Shell.Speed",0f),("Camera.Long",.31f),("Camera.Lat",.28f),("Camera.Yaw",.57f),
            ("Camera.Pitch",.46f),("Camera.Fov",.62f),("Camera.Alt",0f),("Atmosphere.ScaleHeight",.27f),("Camera.Alt",1f)})
        {
            PointSet(a,setting.Item1,setting.Item2);PointSet(b,setting.Item1,setting.Item2);
            a.Render(false);b.Render(false);
            Assert.That(a.ShaderError,Is.Null);Assert.That(b.ShaderError,Is.Null);
            var x=RingReadScene(a);var y=RingReadScene(b);
            double error=0,energy=0;
            for(int i=0;i<x.Length;i++) if(i%4!=3) { error+=Math.Pow(x[i]-y[i],2);energy+=y[i]*y[i]; }
            double rms=Math.Sqrt(error/Math.Max(energy,1e-20));
            TestContext.Out.WriteLine($"{setting}: relative RGB RMS {rms:G6}");
            Assert.That(rms,Is.LessThan(.001));
        }
    }
}

using System;
using System.IO;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [Test, Category("GPU")]
    public void TonalMovementClockAdvancesEquallyWithAndWithoutFluid()
    {
        using var shaders = new SceneShaders("MandelFluid");
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","MandelFluid.hlsl"),"""
            struct FluidSettings { bool PeriodicBoundary; float Viscosity; };
            struct ResourceSizes { uint2 FluidVelocity; };
            cbuffer Parameters { ResourceSizes Sizes = { uint2(128,128) }; FluidSettings Fluid = { true, 0 }; };
            #include "../Common/FluidSimulation.hlsl"
            float2 FluidApplyBoundary(int2 cell, float2 v) { return v; }
            Texture2D<float2> FluidVelocity;
            float4 PS(PSin input) : SV_Target
            { return DisplayOutput(float3(.1*MusicalMovementTime,FluidVelocity.Load(int3(0,0,0)).x,0)); }
            """);
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Clock.hlsl"),"""
            float4 PS(PSin input) : SV_Target { return DisplayOutput(float3(.1*MusicalMovementTime,0,0)); }
            """);
        using var window = new HiddenWindow();
        using var otherWindow = new HiddenWindow();
        var clock = new RenderClock();
        using var fluid = new Renderer(window.Handle,64,36,1,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:"MandelFluid");
        using var plain = new Renderer(otherWindow.Handle,64,36,1,60,shaderDirectory:shaders.Path,timeProvider:clock,initialScene:"Clock");
        for (int frame=0;frame<60;frame++)
        {
            if(frame==20) fluid.SelectScene("Clock");
            if(frame==40) fluid.SelectScene("MandelFluid");
            clock.Advance();
            foreach(var target in new[] { fluid,plain })
            {
                target.PostAudio(new(new[] {.2,0,0,.2},4,DateTime.UnixEpoch) { AnalysisRow=frame },1);
                target.Render(false);
                Assert.That(PointField<double>(target,"movementTime"),Is.EqualTo(target.Audio.MusicalTime).Within(1e-12));
            }
        }
        Assert.That(fluid.ReadPixels(false)[0],Is.GreaterThan(0),"Tonal audio must visibly advance the shader clock.");
        Assert.That(fluid.ReadPixels(false),Is.EqualTo(plain.ReadPixels(false)));
    }
}

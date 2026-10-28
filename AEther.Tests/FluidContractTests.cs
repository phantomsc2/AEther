using System;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [TestCase("MandelFluid", 2), TestCase("MandelFluidv2", 2), TestCase("MandelFluidv3", 2)]
    [TestCase("MandelFluidv4", 2), TestCase("FluidFlight", 1), TestCase("PlaneFlight", 1), Category("GPU")]
    public void FluidScenesUseTheSameSolverWithTheirOwnMaterialLayout(string scene, int attachments)
    {
        using var shaders = new SceneShaders(scene);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 160, 90, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
        var values = renderer.ActiveShader!.Parameters!;
        values.SetBoolean(values.Items.Single(p => p.Name == "Fluid").Children.Single(p => p.Name == "LowQuality"), true);
        clock.Advance(); renderer.Render(false);
        Assert.That(renderer.Fluid.Width, Is.EqualTo(128));
        Assert.That(renderer.Fluid.Height, Is.EqualTo(128));
        Assert.That(renderer.Fluid.MaterialCount, Is.EqualTo(attachments));
        Assert.That(renderer.Fluid.LastMaterialSteps, Is.EqualTo(1));
    }

    const string FluidContractSource = """
        // A comment with #define RING_FLUID must have no special host meaning.
        struct FluidSettings { bool PeriodicBoundary; bool LowQuality; float Viscosity; };
        cbuffer Parameters { FluidSettings Fluid = { true, true, 0 }; };
        #include "../Common/FluidSimulation.hlsl"
        float2 FluidApplyBoundary(int2 cell, float2 velocity) { return velocity; }
        struct Payload
        {
            float Signed : SV_Target0;
            float2 Coordinates : SV_Target1;
            float4 Other : SV_Target2;
        };
        Texture2D<float> FluidMaterial0;
        Texture2D<float2> FluidMaterial1;
        Texture2D<float4> FluidMaterial2;
        Texture2D<float> FluidPredicted0;
        Texture2D<float2> FluidPredicted1;
        Texture2D<float4> FluidPredicted2;
        SamplerState FluidSampler;
        Payload PredictMaterial(PSin input)
        {
            float2 uv = Backtrace(input.UV);
            Payload result;
            result.Signed = FluidMaterial0.SampleLevel(ClampFluid, uv, 0);
            result.Coordinates = FluidMaterial1.SampleLevel(ClampFluid, uv, 0);
            result.Other = FluidMaterial2.SampleLevel(ClampFluid, uv, 0);
            return result;
        }
        Payload UpdateMaterial(PSin input)
        {
            int3 p = int3(int2(input.Position.xy), 0);
            Payload result;
            result.Signed = FluidPredicted0.Load(p) - StepSeconds;
            result.Coordinates = FluidPredicted1.Load(p) + StepSeconds * float2(2, -3);
            result.Other = FluidPredicted2.Load(p) + StepSeconds * float4(4, 5, 6, 7);
            return result;
        }
        float4 PS(PSin input) : SV_Target
        {
            float live = Spectrum0.SampleLevel(Linear, float2(.5, .5), 0).a;
            return float4(live * abs(FluidMaterial0.SampleLevel(FluidSampler, input.UV, 0)), 0, 0, 1);
        }
        """;

    [Test, Category("GPU")]
    public void ShaderDefinedMaterialCanCarrySignedScalarsVectorsAndItsOwnEvolution()
    {
        using var shaders = new SceneShaders("MandelFluidv4");
        File.WriteAllText(Path.Combine(shaders.Path, "Scenes", "MandelFluidv4.hlsl"), FluidContractSource);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 160, 90, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
        for (int frame = 0; frame < 8; frame++) { clock.Advance(); renderer.Render(false); }
        var fluid = renderer.Fluid;
        Assert.That(fluid.MaterialCount, Is.EqualTo(3));
        Assert.That(Enumerable.Range(0, 3).Select(fluid.MaterialChannels), Is.EqualTo(new[] { 1, 2, 4 }));
        Assert.That(fluid.ReadMaterial(0), Is.All.EqualTo(-8f / 60).Within(1e-6), "The engine must not clamp or decay an arbitrary payload.");
        Assert.That(fluid.ReadMaterial(1).Where((_, i) => i % 2 == 0), Is.All.EqualTo(16f / 60).Within(1e-6));
        Assert.That(fluid.ReadMaterial(1).Where((_, i) => i % 2 == 1), Is.All.EqualTo(-24f / 60).Within(1e-6));
        Assert.That(fluid.ReadMaterial(2).Where((_, i) => i % 4 == 3), Is.All.EqualTo(56f / 60).Within(1e-6));
        Assert.That(fluid.ReadVelocity(), Is.All.Zero, "Material evolution must not feed back into flow.");
        Assert.That(renderer.ReadPixels(false).Where((_, i) => i % 4 != 3), Is.All.Zero,
            "Persistent signed material must not require persistent illumination.");
    }

    [Test, Category("GPU")]
    public void FluidCanRunWithAShaderDefinedBoundaryAndNoMaterial()
    {
        using var shaders = new SceneShaders("MandelFluidv4");
        File.WriteAllText(Path.Combine(shaders.Path, "Scenes", "MandelFluidv4.hlsl"), """
            struct FluidSettings { bool PeriodicBoundary; bool LowQuality; float Viscosity; };
            cbuffer Parameters { FluidSettings Fluid = { true, true, 0 }; };
            #include "../Common/FluidSimulation.hlsl"
            float2 FluidApplyBoundary(int2 cell, float2 velocity) { return float2(.5, 0); }
            Texture2D<float2> FluidVelocity;
            SamplerState FluidSampler;
            float4 PS(PSin input) : SV_Target
            {
                float live = Spectrum0.SampleLevel(Linear, float2(.5, .5), 0).a;
                return float4(live * abs(FluidVelocity.SampleLevel(FluidSampler, input.UV, 0)), 0, 1);
            }
            """);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 160, 90, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
        for (int frame = 0; frame < 3; frame++) { clock.Advance(); renderer.Render(false); }
        Assert.That(renderer.Fluid.MaterialCount, Is.Zero);
        Assert.That(renderer.Fluid.LastMaterialSteps, Is.Zero);
        Assert.That(renderer.Fluid.ReadVelocity().Where((_, i) => i % 2 == 0), Is.All.EqualTo(.5f));
        Assert.That(renderer.Fluid.ReadVelocity().Where((_, i) => i % 2 == 1), Is.All.Zero);
    }

    [Test, Category("GPU")]
    public void IncompatibleMaterialOutputsRejectReloadAndKeepTheWorkingScene()
    {
        using var shaders = new SceneShaders("MandelFluidv4");
        var path = Path.Combine(shaders.Path, "Scenes", "MandelFluidv4.hlsl");
        File.WriteAllText(path, FluidContractSource);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 160, 90, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock);
        clock.Advance(); renderer.Render(false);
        var fluid = renderer.Fluid;
        var before = fluid.ReadMaterial(0);
        // Change only the predictor's return type/body; UpdateMaterial keeps its
        // three-output layout so both shaders compile but their contract differs.
        int predict = FluidContractSource.IndexOf("Payload PredictMaterial", StringComparison.Ordinal);
        int update = FluidContractSource.IndexOf("Payload UpdateMaterial", StringComparison.Ordinal);
        var invalid = FluidContractSource[..predict] + "float PredictMaterial(PSin input) : SV_Target { return 0; }\n"
            + FluidContractSource[update..];
        File.WriteAllText(path, invalid);
        Assert.That(renderer.ReloadActiveScene(), Is.False);
        Assert.That(renderer.ShaderError, Does.Contain("matching"));
        Assert.That(renderer.Fluid, Is.SameAs(fluid));
        Assert.That(fluid.ReadMaterial(0), Is.EqualTo(before));
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.ReadMaterial(0).Min(), Is.LessThan(before.Min()));
    }
}

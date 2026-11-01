using System;
using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [Test, Category("GPU")]
    public void WarmPressureUsesTwoCyclesAndResetsOnBoundaryChangesAndClear()
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, 8, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        var effect = (FluidEffect)renderer.ActiveShader!.Effects[0];
        effect.Options = new FluidOptions(128, 64);
        clock.Advance(); renderer.Render(false);
        var fluid = effect.Simulation!;
        clock.Advance(); renderer.Render(false);
        int warm = fluid.LastDraws;
        var periodic = effect.Parameters!.Items.Single(p => p.Name == "Fluid").Children.Single(p => p.Name == "PeriodicBoundary");
        effect.Parameters.SetBoolean(periodic, false);
        clock.Advance(); renderer.Render(false);
        int cold = fluid.LastDraws;
        Assert.That(cold, Is.GreaterThan(warm));
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.LastDraws, Is.EqualTo(warm));
        fluid.Clear();
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.ReadVelocity().All(float.IsFinite), Is.True);
        effect.Parameters.SetBoolean(periodic, true);
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.LastDraws, Is.EqualTo(cold));
        var warmStart = effect.Parameters.Items.Single(p => p.Name == "Fluid").Children.Single(p => p.Name == "WarmStart");
        Assert.That(effect.Parameters.GetBoolean(warmStart), Is.True);
        effect.Parameters.SetBoolean(warmStart, false);
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.LastDraws, Is.EqualTo(cold));
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.LastDraws, Is.EqualTo(cold), "Disabling warm starts keeps three cold cycles on every step.");
        effect.Parameters.SetBoolean(warmStart, true);
        clock.Advance(); renderer.Render(false);
        Assert.That(effect.Simulation, Is.SameAs(fluid));
        Assert.That(fluid.LastDraws, Is.EqualTo(warm));
        effect.ReferencePressureSolver = true;
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.LastDraws, Is.EqualTo(cold));
    }
    [TestCase(128, 64), TestCase(1024, 512), Category("GPU")]
    public void PeriodicMaskMatchesSignedModuloThroughFluidTransport(int width, int height)
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var referenceShaders = new SceneShaders("MandelFluid");
        string file = Path.Combine(referenceShaders.Path, "Common", "FluidSimulation.hlsl");
        string source = File.ReadAllText(file);
        string reference = source.Replace("return p & (int2(GridSize) - 1);",
            "int2 size = int2(GridSize); return (p % size + size) % size;");
        Assert.That(reference, Is.Not.EqualTo(source));
        File.WriteAllText(file, reference);
        using var window = new HiddenWindow();
        using var referenceWindow = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, 8, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        using var baseline = new Renderer(referenceWindow.Handle, 1280, 720, 8, 60,
            shaderDirectory: referenceShaders.Path, timeProvider: clock, initialScene: referenceShaders.Scene);
        foreach (var target in new[] { renderer, baseline })
            ((FluidEffect)target.ActiveShader!.Effects[0]).Options = new FluidOptions(width, height);
        for (int frame = 0; frame < 60; frame++)
        {
            var values = new double[8 * 8];
            for (int i = 0; i < values.Length; i += 4)
            {
                values[i] = .1 + .1 * Math.Sin(i + frame * .1);
                values[i + 1] = frame % 12 < 3 ? .12 : 0;
                values[i + 2] = .08;
            }
            foreach (var target in new[] { renderer, baseline })
                target.PostAudio(new SampleEvent<double>(values, 32, DateTime.UnixEpoch) { AnalysisRow = frame }, 2);
            clock.Advance(); renderer.Render(false); baseline.Render(false);
        }
        var actual = ((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!;
        var expected = ((FluidEffect)baseline.ActiveShader!.Effects[0]).Simulation!;
        Assert.That(actual.ReadVelocity(), Is.EqualTo(expected.ReadVelocity()));
        for (int i = 0; i < actual.MaterialCount; i++)
            Assert.That(actual.ReadMaterial(i), Is.EqualTo(expected.ReadMaterial(i)));
        Assert.That(renderer.ReadPixels(false), Is.EqualTo(baseline.ReadPixels(false)));
    }

    [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true), Category("GPU")]
    public void ConfinementActsOnVorticesAndLeavesZeroFlowUnchanged(bool vc1, bool vc2)
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        clock.Advance(); renderer.Render(false);
        var effect = (FluidEffect)renderer.ActiveShader!.Effects[0];
        var fluid = effect.Simulation!;
        var zero = new float[fluid.Width * fluid.Height * 2];
        fluid.WriteVelocity(zero);
        fluid.CheckConfinement(effect.Programs, vc1 ? .5f : 0, vc2 ? .5f : 0);
        Assert.That(fluid.ReadVelocity(), Is.All.Zero);

        var initial = new float[zero.Length];
        for (int y = 0; y < fluid.Height; y++)
        for (int x = 0; x < fluid.Width; x++)
        {
            int i = 2 * (y * fluid.Width + x);
            initial[i] = (float)(Math.Sin(2 * Math.PI * y / fluid.Height) * Math.Cos(4 * Math.PI * x / fluid.Width));
            initial[i + 1] = (float)(-Math.Sin(2 * Math.PI * x / fluid.Width) * Math.Cos(4 * Math.PI * y / fluid.Height));
        }
        fluid.WriteVelocity(initial);
        fluid.CheckConfinement(effect.Programs, vc1 ? .5f : 0, vc2 ? .5f : 0);
        var result = fluid.ReadVelocity();
        Assert.That(result.All(float.IsFinite), Is.True);
        double delta = result.Zip(initial, (a, b) => Math.Abs((double)a - b)).Sum();
        Assert.That(delta, vc1 || vc2 ? Is.GreaterThan(1e-5) : Is.Zero);
        if (!vc1 && vc2)
        {
            Assert.That(Math.Abs(result.Where((_, i) => i % 2 == 0).Sum(v => (double)v)
                                 - initial.Where((_, i) => i % 2 == 0).Sum(v => (double)v)), Is.LessThan(.01));
            Assert.That(Math.Abs(result.Where((_, i) => i % 2 == 1).Sum(v => (double)v)
                                 - initial.Where((_, i) => i % 2 == 1).Sum(v => (double)v)), Is.LessThan(.01));
        }
    }

    [Test, Category("GPU")]
    public void ConfinementControlsChangePassesWithoutResettingSimulation()
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        clock.Advance(); renderer.Render(false);
        var effect = (FluidEffect)renderer.ActiveShader!.Effects[0];
        var fluid = effect.Simulation!;
        var parameters = effect.Parameters!;
        var fields = parameters.Items.Single(p => p.Name == "Fluid").Children;
        ShaderParameter Field(string name) => fields.Single(p => p.Name == name);
        Assert.That(parameters.GetBoolean(Field("VC1Enabled")), Is.False);
        Assert.That(parameters.GetBoolean(Field("VC2Enabled")), Is.False);
        Assert.That(parameters.GetFloat(Field("VC1Strength")), Is.EqualTo(.5f));
        Assert.That(parameters.GetFloat(Field("VC2Strength")), Is.EqualTo(.507f));
        clock.Advance(); renderer.Render(false);
        int baseline = fluid.LastDraws;
        parameters.SetBoolean(Field("VC1Enabled"), true);
        clock.Advance(); renderer.Render(false);
        Assert.That(effect.Simulation, Is.SameAs(fluid));
        Assert.That(fluid.LastDraws, Is.EqualTo(baseline + 2));
        parameters.SetBoolean(Field("VC2Enabled"), true);
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.LastDraws, Is.EqualTo(baseline + 5));
        parameters.SetFloat(Field("VC1Strength"), 0);
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.LastDraws, Is.EqualTo(baseline + 3));
        parameters.SetBoolean(Field("VC2Enabled"), false);
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.LastDraws, Is.EqualTo(baseline));
    }

    [TestCase(false), TestCase(true), Category("GPU")]
    public void ConfinementRemainsFiniteAtMaximumStrengthOnNonSquareGrid(bool periodic)
    {
        using var shaders = new SceneShaders("MandelFluid");
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 1280, 720, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene);
        var effect = (FluidEffect)renderer.ActiveShader!.Effects[0];
        effect.Options = new FluidOptions(128, 64);
        var parameters = effect.Parameters!;
        var fields = parameters.Items.Single(p => p.Name == "Fluid").Children;
        parameters.SetBoolean(fields.Single(p => p.Name == "PeriodicBoundary"), periodic);
        clock.Advance(); renderer.Render(false);
        var fluid = effect.Simulation!;
        var initial = new float[fluid.Width * fluid.Height * 2];
        for (int y = 0; y < fluid.Height; y++)
        for (int x = 0; x < fluid.Width; x++)
        {
            int i = 2 * (y * fluid.Width + x);
            initial[i] = (float)(.2 * Math.Sin(2 * Math.PI * y / fluid.Height));
            initial[i + 1] = (float)(.2 * Math.Sin(2 * Math.PI * x / fluid.Width));
        }
        fluid.WriteVelocity(initial);
        for (int i = 0; i < 60; i++) fluid.CheckConfinement(effect.Programs, 1, 1);
        var result = fluid.ReadVelocity();
        Assert.That(result.All(float.IsFinite), Is.True);
        Assert.That(result.Max(v => Math.Abs(v)), Is.LessThan(10));
        if (!periodic)
        {
            for (int y = 0; y < fluid.Height; y++) Assert.That(result[2 * (y * fluid.Width + fluid.Width - 1)], Is.Zero);
            for (int x = 0; x < fluid.Width; x++) Assert.That(result[2 * ((fluid.Height - 1) * fluid.Width + x) + 1], Is.Zero);
        }
    }


    const string FluidContractSource = """
        // A comment with #define RING_FLUID must have no special host meaning.
        struct FluidSettings { bool PeriodicBoundary; float Viscosity; };

        cbuffer Parameters { FluidSettings Fluid = { true, 0 }; };
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
        using var shaders = new SceneShaders("MandelFluid");
        File.WriteAllText(Path.Combine(shaders.Path, "Scenes", "MandelFluid.hlsl"), FluidContractSource);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 160, 90, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene, sceneDefinitions: [FluidDefinition(global::Vortice.DXGI.Format.R32_Float, global::Vortice.DXGI.Format.R32G32_Float, global::Vortice.DXGI.Format.R32G32B32A32_Float)]);
        for (int frame = 0; frame < 8; frame++) { clock.Advance(); renderer.Render(false); }
        var fluid = ((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!;
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
        using var shaders = new SceneShaders("MandelFluid");
        File.WriteAllText(Path.Combine(shaders.Path, "Scenes", "MandelFluid.hlsl"), """
            struct FluidSettings { bool PeriodicBoundary; float Viscosity; };

            cbuffer Parameters { FluidSettings Fluid = { true, 0 }; };
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
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene, sceneDefinitions: [FluidDefinition()]);
        renderer.Render(false);
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.LastSteps, Is.EqualTo(2), "Initialize shader-defined flow without waiting for audio.");
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.ReadVelocity().Where((_, i) => i % 2 == 0), Is.All.EqualTo(.5f));
        for (int frame = 0; frame < 3; frame++) { clock.Advance(); renderer.Render(false); }
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.MaterialCount, Is.Zero);
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.LastMaterialSteps, Is.Zero);
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.ReadVelocity().Where((_, i) => i % 2 == 0), Is.All.EqualTo(.5f));
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!.ReadVelocity().Where((_, i) => i % 2 == 1), Is.All.Zero);
    }

    [Test, Category("GPU")]
    public void IncompatibleMaterialOutputsRejectReloadAndKeepTheWorkingScene()
    {
        using var shaders = new SceneShaders("MandelFluid");
        var path = Path.Combine(shaders.Path, "Scenes", "MandelFluid.hlsl");
        File.WriteAllText(path, FluidContractSource);
        using var window = new HiddenWindow();
        var clock = new RenderClock();
        using var renderer = new Renderer(window.Handle, 160, 90, Bands, 60,
            shaderDirectory: shaders.Path, timeProvider: clock, initialScene: shaders.Scene, sceneDefinitions: [FluidDefinition(global::Vortice.DXGI.Format.R32_Float, global::Vortice.DXGI.Format.R32G32_Float, global::Vortice.DXGI.Format.R32G32B32A32_Float)]);
        clock.Advance(); renderer.Render(false);
        var fluid = ((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!;
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
        Assert.That(((FluidEffect)renderer.ActiveShader!.Effects[0]).Simulation!, Is.SameAs(fluid));
        Assert.That(fluid.ReadMaterial(0), Is.EqualTo(before));
        clock.Advance(); renderer.Render(false);
        Assert.That(fluid.ReadMaterial(0).Min(), Is.LessThan(before.Min()));
    }
}

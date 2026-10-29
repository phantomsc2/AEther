using System.IO;
using System.Linq;
using AEther.Vortice;
using NUnit.Framework;

namespace AEther.Tests;

public partial class ShaderRenderingTests
{
    [Test, Category("GPU")]
    public void EmptySelectionSurvivesReloadAndDeletingTheSelectedScene()
    {
        using var shaders = new SceneShaders("Spectrum");
        using var window = new HiddenWindow();
        using var renderer = new Renderer(window.Handle,64,36,8,60,shaderDirectory:shaders.Path);
        Assert.That(renderer.SceneName,Is.Null);
        Assert.That(renderer.ActiveShader,Is.Null);
        Assert.That(renderer.Scenes.All(x=>!x.ProgramReady),Is.True);
        Assert.DoesNotThrow(()=>renderer.Render(false));
        Assert.That(renderer.CompileActiveScene(),Is.False);
        Assert.That(renderer.ReloadActiveScene(),Is.False);
        Assert.That(renderer.ReloadShaders(),Is.True);
        Assert.That(renderer.SceneName,Is.Null);
        renderer.SelectScene("Spectrum"); renderer.Render(false);
        renderer.SelectScene(null);
        Assert.That(renderer.ReloadShaders(),Is.True);
        Assert.That(renderer.SceneName,Is.Null);
        renderer.SelectScene("Spectrum");
        File.Copy(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"),Path.Combine(shaders.Path,"Scenes","Other.hlsl"));
        File.Delete(Path.Combine(shaders.Path,"Scenes","Spectrum.hlsl"));
        Assert.That(renderer.ReloadShaders(),Is.True);
        Assert.That(renderer.SceneName,Is.Null,"Deleting a selected scene must not select another scene.");
        File.Delete(Path.Combine(shaders.Path,"Scenes","Other.hlsl"));
        Assert.That(renderer.ReloadShaders(),Is.True);
        Assert.That(renderer.Scenes,Is.Empty);
    }

    [Test, Category("GPU")]
    public void AnalysisPreviewUsesPrivateDefaultsWithoutSelectingOrRestoringSceneSettings()
    {
        using var shaders = new SceneShaders("Spectrum");
        File.WriteAllText(Path.Combine(shaders.Path,"Scenes","Debug.hlsl"),"""
            cbuffer Parameters { bool Toggle = false; };
            float4 PS(PSin input) : SV_Target { return DisplayOutput(Toggle ? float3(1,0,0) : float3(0,1,0)); }
            """);
        using var window = new HiddenWindow();
        using var preview = new HiddenWindow();
        using var renderer = new Renderer(window.Handle,64,36,8,60,shaderDirectory:shaders.Path);
        renderer.AttachDebugOutput(preview.Handle,64,36);
        renderer.Render(false,renderDebug:true);
        Assert.That(renderer.SceneName,Is.Null);
        Assert.That(renderer.GetSceneParameters("Debug"),Is.Null,"Preview settings must remain private.");
        var original=renderer.ReadDebugPixels();
        Assert.That(original[1],Is.EqualTo(255));
        renderer.SelectScene("Debug");
        renderer.ActiveShader!.Parameters!.SetBoolean(renderer.ActiveShader.Parameters.Items.Single(),true);
        renderer.Render(false,renderDebug:true);
        Assert.That(renderer.ReadPixels(false)[0],Is.EqualTo(255));
        Assert.That(renderer.ReadDebugPixels(),Is.EqualTo(original));
        renderer.SelectScene(null);
        Assert.That(renderer.ReloadShaders(),Is.True);
        renderer.DetachDebugOutput(); renderer.AttachDebugOutput(preview.Handle,64,36);
        renderer.Render(false,renderDebug:true);
        Assert.That(renderer.ReadDebugPixels(),Is.EqualTo(original));
        Assert.That(renderer.SceneName,Is.Null);
    }
}

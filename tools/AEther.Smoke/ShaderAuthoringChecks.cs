using System;
using System.IO;
using System.Linq;
using AEther.Vortice;

internal static partial class Program
{
    static void CopyShaderTree(string source, string destination)
    {
        foreach (var file in Directory.GetFiles(source, "*.hlsl", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    static ShaderParameter Find(ShaderParameters parameters, string path)
    {
        var items = parameters.Items;
        ShaderParameter? result = null;
        foreach (var segment in path.Split('.')) { result = items.Single(p => p.Name == segment); items = result.Children; }
        return result!;
    }

    static void CheckShaderAuthoring(IntPtr window, string shaders, string output)
    {
        using var renderer = new Renderer(window, 320, 180, 64, 256, shaderDirectory: shaders);
        var expected = Directory.GetFiles(Path.Combine(shaders, "Scenes"), "*.hlsl")
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal);
        if (!renderer.Scenes.Select(s => s.Name).Order(StringComparer.Ordinal).SequenceEqual(expected))
            throw new Exception("Discovered scenes do not match the scene files.");
        foreach (var scene in renderer.Scenes)
        {
            renderer.SelectScene(scene.Name); Seed(renderer); renderer.Render(false);
            var pixels = renderer.ReadPixels();
            if (!pixels.Where((_, i) => i % 4 != 3).Any(v => v > 10)) throw new Exception($"{scene.Name} is blank.");
            Save(pixels, 320, 180, Path.Combine(output, $"authoring-{scene.Name}.png"));
        }
        renderer.Resize(300, 170); renderer.Render(false);
        if (renderer.ReadPixels().Length != 300 * 170 * 4) throw new Exception("Resize failed.");

        // No source parser: nested defaults, bool storage, vector packing and binding slots are all reflected.
        var path = Path.Combine(shaders, "Scenes", "AuthoringProbe.hlsl");
        const string initial = """
            struct Settings { float Gain; bool Enabled; float3 Tint; };
            cbuffer Parameters : register(b7) { Settings Controls = { 0.25, true, float3(0.2, 0.4, 0.6) }; };
            float4 PS(PSin input) : SV_TARGET { return float4(Controls.Enabled ? Controls.Tint * Controls.Gain : 0, 1); }
            """;
        File.WriteAllText(path, initial);
        if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
        renderer.SelectScene("AuthoringProbe");
        var parameters = renderer.ActiveShader!.Parameters!;
        if (Math.Abs(parameters.GetFloat(Find(parameters, "Controls.Gain")) - .25f) > 1e-6
            || !parameters.GetBoolean(Find(parameters, "Controls.Enabled"))) throw new Exception("Reflected defaults are incorrect.");
        parameters.SetFloat(Find(parameters, "Controls.Gain"), .75f);
        renderer.Render(false);
        var visible = renderer.ReadPixels(false);
        parameters.SetBoolean(Find(parameters, "Controls.Enabled"), false);
        renderer.Render(false);
        if (renderer.ReadPixels(false).Where((_, i) => i % 4 != 3).Any(v => v != 0)) throw new Exception("Boolean upload failed.");
        parameters.SetBoolean(Find(parameters, "Controls.Enabled"), true);

        // Move both the buffer slot and member offsets. Values must follow names and types.
        File.WriteAllText(path, initial.Replace("float Gain; bool Enabled; float3 Tint;", "float3 Tint; bool Enabled; float Gain;")
            .Replace("0.25, true, float3(0.2, 0.4, 0.6)", "float3(0.2, 0.4, 0.6), true, 0.25").Replace("register(b7)", "register(b4)"));
        if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
        parameters = renderer.ActiveShader!.Parameters!;
        if (parameters.GetFloat(Find(parameters, "Controls.Gain")) != .75f) throw new Exception("Reordering lost parameter values.");
        renderer.Render(false);
        if (!visible.SequenceEqual(renderer.ReadPixels(false))) throw new Exception("Reordered buffer changed rendering.");

        foreach (var broken in new[]
        {
            "THIS IS INVALID HLSL",
            "cbuffer Parameters { int Unsupported; }; float4 PS(PSin i):SV_TARGET { return Unsupported; }",
            "Texture2D Unknown; float4 PS(PSin i):SV_TARGET { return Unknown.Load(int3(0,0,0)); }",
        })
        {
            File.WriteAllText(path, broken);
            var before = renderer.ActiveShader;
            if (renderer.ReloadShaders() || renderer.ActiveShader != before) throw new Exception("Failed reload replaced a working scene.");
            renderer.Render(false);
        }
        File.WriteAllText(path, initial);
        if (!renderer.ReloadShaders()) throw new Exception(renderer.ShaderError);
        parameters = renderer.ActiveShader!.Parameters!; parameters.Reset();
        if (parameters.GetFloat(Find(parameters, "Controls.Gain")) != .25f) throw new Exception("Reset did not use source defaults.");
        File.Delete(path);
        if (!renderer.ReloadShaders() || renderer.Scenes.Any(s => s.Name == "AuthoringProbe") || renderer.ActiveShader != null)
            throw new Exception("Deleting the active scene failed to clear selection.");
        Console.WriteLine("Shader authoring: scene discovery, GPU output, nested defaults, vectors, bools, nonstandard slots, reordered layout, reload rollback, reset, add/remove and resize passed.");
    }

}

using System;
using System.IO;
using System.Linq;
using AEther.Vortice;

internal static partial class Program
{
    static void CheckFlameNormalization(IntPtr window, string shaders)
    {
        using var renderer = new Renderer(window, 1280, 720, 64, 60,
            shaderDirectory: shaders, initialScene: "Flame");
        if (renderer.ShaderError != null) throw new Exception(renderer.ShaderError);
        float[] Read() => ((FlameEffect)renderer.ActiveShader!.Effects[0]).ReadDensityPixels();
        renderer.Render(false);
        if (Read().Length != 2048 * 1024) throw new Exception("Flame did not use power-of-two internal dimensions.");
        for (int i = 0; i < 23; i++) renderer.Render(false);
        var density = Read();
        if (density.Any(v => !float.IsFinite(v) || v < 0) || density.Max() <= 0)
            throw new Exception("Normalized Flame iteration produced invalid density.");
        renderer.Resize(1281, 721);
        renderer.Render(false);
        if (Read().Length != 2048 * 1024) throw new Exception("Flame changed dimensions within the same power-of-two bucket.");
        renderer.Resize(2049, 721);
        renderer.Render(false);
        if (Read().Length != 4096 * 1024) throw new Exception("Flame did not advance to the next power-of-two width.");

        var shader = Path.Combine(shaders, "Scenes", "Flame.hlsl");
        var original = File.ReadAllText(shader);
        const string expression = "return mean > 0 ? density / (96 * mean) : 0;";
        if (!original.Contains(expression)) throw new Exception("Flame normalization expression changed.");
        try
        {
            File.WriteAllText(shader, original.Replace(expression, "return mean > 0 ? 1 / (96 * mean) : 0;"));
            if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
            renderer.Resize(1280, 720);
            renderer.Render(false);
            if (Math.Abs(Read()[0] - 1) > .00001) throw new Exception("Seed 1x1 mean was not read on the GPU.");
            renderer.Render(false);
            if (Math.Abs(Read()[0] - 1f / 96) > .000001) throw new Exception("Updated 1x1 mean was not read on the GPU.");
        }
        finally
        {
            File.WriteAllText(shader, original);
            if (!renderer.ReloadActiveScene()) throw new Exception(renderer.ShaderError);
        }
        Console.WriteLine("Flame normalization: power-of-two dimensions, GPU 1x1 mean and 24 live iterations passed.");
    }
}

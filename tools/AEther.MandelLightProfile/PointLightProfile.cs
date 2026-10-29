using System.Diagnostics;
using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static long rasterAudioRow;
    static void RunPointLightProfile(string[] args)
    {
        var positional = args.Where(x => !x.StartsWith("--")).ToArray();
        string source = Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output = Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/mandellight");
        string tree = Path.Combine(output, "Shaders-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tree, "Scenes"));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "Common"), "*.hlsl", SearchOption.AllDirectories))
        {
            string target = Path.Combine(tree, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
        foreach (string scene in new[] { "MandelLight" })
            File.Copy(Path.Combine(source, "Scenes", scene + ".hlsl"), Path.Combine(tree, "Scenes", scene + ".hlsl"), true);
        using var window = new Form { ClientSize = new(1280, 720), ShowInTaskbar = false };
        var clock = new Clock();
        using var renderer = new Renderer(window.Handle, 1280, 720, 32, 60, shaderDirectory: tree, timeProvider: clock);
        var device = Field<ID3D11Device>(renderer, "device");
        var context = Field<ID3D11DeviceContext>(renderer, "context");
        var results = new List<object>();
        foreach (var item in new[]
        {
            (Name:"s", Scene:"MandelLight", S:.02, T:0.0, B:0.0),
            (Name:"t", Scene:"MandelLight", S:0.0, T:.02, B:0.0),
            (Name:"b", Scene:"MandelLight", S:0.0, T:0.0, B:.02),
            (Name:"stb", Scene:"MandelLight", S:.02, T:.02, B:.02),
        })
        {
            renderer.SelectScene(item.Scene); renderer.ActiveShader!.Parameters!.Reset();
            Set(renderer, "View.CameraSpeed", 0); Set(renderer, "Shape.Speed", 0);
            clock.Reset();
            void Render()
            {
                int bands = renderer.Audio.Bands; var spectrum = new double[8*bands];
                for (int channel = 0; channel < 2; channel++) for (int band = 0; band < bands; band++)
                {
                    double f = (band+.5)/bands, shape = .25+.75*Math.Pow(.5+.5*Math.Cos(10*f+channel*1.7), 2);
                    int at = 4*(channel*bands+band);
                    spectrum[at] = item.S*shape; spectrum[at+1] = item.T*shape; spectrum[at+2] = item.B*shape;
                }
                renderer.PostAudio(new(spectrum, 4*bands, DateTime.UnixEpoch) { AnalysisRow = ++rasterAudioRow }, 2);
                clock.Advance(); renderer.Render(false);
            }
            for (int i = 0; i < 16; i++)
            {
                Render(); renderer.ReadPixels(false);
            }
            var frames = new List<Dictionary<string, double>>();
            using var timer = new Timestamps(device, context);
            for (int i = 0; i < 41; i++)
            {
                renderer.TransportPassMarker = null; Render(); Render();
                timer.Reset(); renderer.TransportPassMarker = timer.Marker;
                timer.Begin(); Render(); timer.End(); frames.Add(timer.Resolve());
            }
            renderer.TransportPassMarker = null;
            var medians = frames.SelectMany(x => x.Keys).Distinct().ToDictionary(k => k, k => Median(frames.Select(x => x.GetValueOrDefault(k))));
            Save(renderer.ReadPixels(false), 1280, 720, Path.Combine(output, item.Name + ".png"));
            Console.WriteLine(item.Name + ": " + string.Join(", ", medians.Select(x => $"{x.Key}={x.Value:F3}ms")));
            results.Add(new { item.Name, item.Scene, width = 1280, height = 720, milliseconds = medians });
        }
        File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(new
        {
            adapter = renderer.AdapterDescription,
            note = "720p, 128 fixed cubes, stationary camera, full-resolution airlight, GPU atmosphere LUT built. 41 frames after warmup; two untimed renders before each measurement. Uses 3x60 GPU-generated point lights. Total includes output, excluding readback, compilation and LUT generation.",
            results,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

using System.Diagnostics;
using System.Text.Json;
using AEther.Vortice;
using Vortice.Direct3D11;

internal static partial class Program
{
    static void RunPointLightProfile(string[] args)
    {
        var positional = args.Where(x => !x.StartsWith("--")).ToArray();
        string source = Path.GetFullPath(positional.ElementAtOrDefault(0) ?? "AEther.Vortice/Shaders");
        string output = Path.GetFullPath(positional.ElementAtOrDefault(1) ?? "artifacts/mandellight-v5");
        int width = args.Contains("--1440p") ? 2560 : args.Contains("--1080p") ? 1920 : 1280;
        int height = width * 9 / 16;
        string tree = Path.Combine(output, "Shaders");
        Directory.CreateDirectory(Path.Combine(tree, "Scenes"));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "Common"), "*.hlsl", SearchOption.AllDirectories))
        {
            string target = Path.Combine(tree, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
        foreach (string scene in new[] { "MandelLightv4", "MandelLightv5" })
            File.Copy(Path.Combine(source, "Scenes", scene + ".hlsl"), Path.Combine(tree, "Scenes", scene + ".hlsl"), true);
        using var window = new Form { ClientSize = new(width, height), ShowInTaskbar = false };
        var clock = new Clock();
        using var renderer = new Renderer(window.Handle, width, height, 32, 60, shaderDirectory: tree, timeProvider: clock);
        var device = Field<ID3D11Device>(renderer, "device");
        var context = Field<ID3D11DeviceContext>(renderer, "context");
        var results = new List<object>();
        foreach (var item in new[]
        {
            (Name:"s", Scene:"MandelLightv5", S:.02, T:0.0, B:0.0),
            (Name:"t", Scene:"MandelLightv5", S:0.0, T:.02, B:0.0),
            (Name:"b", Scene:"MandelLightv5", S:0.0, T:0.0, B:.02),
            (Name:"stb", Scene:"MandelLightv5", S:.02, T:.02, B:.02),
            (Name:"stb-exhaustive", Scene:"MandelLightv5", S:.02, T:.02, B:.02),
            (Name:"stb-288", Scene:"MandelLightv5", S:.02, T:.02, B:.02),
            (Name:"stb-288-exhaustive", Scene:"MandelLightv5", S:.02, T:.02, B:.02),
            (Name:"v4-point-180", Scene:"MandelLightv4", S:.02, T:.02, B:0.0),
        })
        {
            if (args.Contains("--sampling-only") && !item.Name.StartsWith("stb")) continue;
            renderer.SelectScene(item.Scene); renderer.ActiveShader!.Parameters!.Reset();
            Set(renderer, "View.CameraSpeed", 0); Set(renderer, "Shape.Speed", 0);
            if (item.Scene == "MandelLightv5")
            {
                Set(renderer, "Lights.SampleAirlight", !item.Name.Contains("exhaustive"));
                if (item.Name.Contains("288")) Set(renderer, "Lights.Count", 1f);
            }
            if (item.Scene == "MandelLightv4")
            {
                Set(renderer, "Lights.Count", 89f/95); Set(renderer, "Lights.Directional", false);
                Set(renderer, "RingLight.Intensity", 0);
                Set(renderer, "Quality.HalfResolutionAirlight", false);
            }
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
                renderer.PostAudio(new(spectrum, 4*bands, DateTime.UnixEpoch) { AnalysisRow = ++v4AudioRow }, 2);
                clock.Advance(); renderer.Render(false);
            }
            var wait = Stopwatch.StartNew();
            for (int i = 0; i < 16 || !renderer.UnshadowedAirlightReady; i++)
            {
                Render(); renderer.ReadPixels(false);
                if (wait.Elapsed.TotalSeconds > 20) throw new TimeoutException("Atmosphere LUT warmup timed out.");
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
            if (medians.Keys.Any(x => x.StartsWith("Raster light "))) throw new InvalidOperationException("Unexpected shadow draw.");
            Save(renderer.ReadPixels(false), width, height, Path.Combine(output, item.Name + ".png"));
            Console.WriteLine(item.Name + ": " + string.Join(", ", medians.Select(x => $"{x.Key}={x.Value:F3}ms")));
            results.Add(new { item.Name, item.Scene, width, height, milliseconds = medians });
        }
        File.WriteAllText(Path.Combine(output, "timings.json"), JsonSerializer.Serialize(new
        {
            adapter = renderer.AdapterDescription,
            note = $"{width}x{height}, 128 fixed cubes, stationary camera, full-resolution airlight, shared atmosphere LUT ready. 41 frames after warmup; two untimed renders before each measurement. V5 uses a coarse per-light RGB baseline plus M=2 residual samples per 16-pixel grid node, with 5% relative-depth compatibility and ordinary sampling where all corners are rejected; 3x60 or 3x96 lights; exhaustive cases disable airlight sampling. Surfaces always sum all lights. V4 uses 2x90 unshadowed point lights, no filament. Different v4/v5 layouts and inputs: cost comparison only. Total includes output, excluding readback, compilation and LUT generation.",
            results,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}

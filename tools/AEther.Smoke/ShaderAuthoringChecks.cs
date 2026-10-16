using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
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
        if (renderer.Scenes.Count != 4) throw new Exception("Expected four discovered scenes.");
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
        if (!renderer.ReloadShaders() || renderer.Scenes.Any(s => s.Name == "AuthoringProbe") || renderer.ActiveShader == null)
            throw new Exception("Deleting the active scene failed to select a fallback.");
        Console.WriteLine("Shader authoring: four scenes, GPU output, nested defaults, vectors, bools, nonstandard slots, reordered layout, reload rollback, reset, add/remove and resize passed.");
    }

    static void CheckShaderWindows(string? shaders)
    {
        var started = 0;
        using var host = new AEther.WindowsForms.MainForm(() => new SyntheticSource(() => started++), shaders)
            { Opacity = 0, ShowInTaskbar = false };
        var settings = host.OwnedForms.OfType<AEther.WindowsForms.SettingsForm>().Single();
        var shaderWindow = host.OwnedForms.OfType<AEther.WindowsForms.ShaderForm>().Single();
        settings.Opacity = 0; shaderWindow.Opacity = 0;
        var sessionField = typeof(AEther.WindowsForms.MainForm).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        AEther.WindowsForms.Session? original = null;
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        var stage = 0;
        Exception? failure = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        void Key(Form form, Keys key) => form.GetType().GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { default(Message), key });
        var added = shaders == null ? null : Path.Combine(shaders, "Scenes", "WatcherProbe.hlsl");
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Shader window check timed out.");
                if (started == 0) return;
                var current = (AEther.WindowsForms.Session)sessionField.GetValue(host)!;
                switch (stage)
                {
                    case 0:
                        original = current;
                        Key(host, Keys.Space);
                        if (!settings.Visible) throw new Exception("Settings did not open.");
                        if (settings.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<PropertyGrid>().Count() != 1)
                            throw new Exception("Shader-specific properties remain in config UI.");
                        var selector = Descendants(settings).OfType<ComboBox>().Single(c => c.Name == "Scene");
                        selector.SelectedItem = "Spectrum";
                        selector.SelectedItem = "Mandelbox";
                        if (!shaderWindow.Visible || shaderWindow.Owner != host) throw new Exception("Selecting a scene did not open the shader window.");
                        if (Descendants(shaderWindow).OfType<ComboBox>().Any()) throw new Exception("Scene selector remains in shader window.");
                        Key(host, Keys.F6);
                        if (shaderWindow.Visible) throw new Exception("F6 did not hide the shader window from the render window.");
                        Key(settings, Keys.F6);
                        if (!shaderWindow.Visible) throw new Exception("F6 did not reopen the shader window from settings.");
                        Key(shaderWindow, Keys.F6);
                        if (shaderWindow.Visible) throw new Exception("F6 did not hide the focused shader window.");
                        selector.SelectedItem = "Spectrum";
                        selector.SelectedItem = "Mandelbox";
                        if (!shaderWindow.Visible) throw new Exception("Selecting another scene did not reopen the shader window.");
                        if (current.Renderer.SceneName != "Mandelbox") throw new Exception("Scene selection failed.");
                        var controls = Descendants(shaderWindow).ToArray();
                        if (!controls.OfType<GroupBox>().Any(g => g.Text == "Geometry") || !controls.OfType<TrackBar>().Any())
                            throw new Exception("Dynamic parameter tree is missing.");
                        selector.SelectedItem = "Spectrum";
                        var checkbox = Descendants(shaderWindow).OfType<CheckBox>().Single(c => c.Text == "CombinedLevel");
                        checkbox.Checked = true;
                        var values = current.Renderer.ActiveShader!.Parameters!;
                        if (!values.GetBoolean(Find(values, "CombinedLevel"))) throw new Exception("Checkbox did not update the shader.");
                        Key(host, Keys.F6);
                        if (added != null) File.WriteAllText(added, "float4 PS(PSin i):SV_TARGET { return float4(0.2,0.3,0.4,1); }");
                        stage++;
                        break;
                    case 1:
                        if (added != null && !current.Renderer.Scenes.Any(s => s.Name == "WatcherProbe")) return;
                        if (started != 1 || current != original) throw new Exception("Shader editing restarted audio capture.");
                        if (added != null)
                        {
                            var dropdown = Descendants(settings).OfType<ComboBox>().Single(c => c.Name == "Scene");
                            if (!dropdown.Items.Contains("WatcherProbe")) throw new Exception("Watcher failed to refresh scene UI.");
                            File.Delete(added);
                        }
                        if (shaderWindow.Visible) throw new Exception("Refreshing the scene list reopened a hidden shader window.");
                        var sceneSelector = Descendants(settings).OfType<ComboBox>().Single(c => c.Name == "Scene");
                        sceneSelector.SelectedItem = "Mandelbox";
                        if (!shaderWindow.Visible) throw new Exception("Scene selection did not reopen the hidden shader window.");
                        shaderWindow.Close();
                        if (shaderWindow.IsDisposed || shaderWindow.Visible) throw new Exception("Closing shader window did not hide it.");
                        Key(host, Keys.F6);
                        if (!shaderWindow.Visible) throw new Exception("Shader window failed to reopen.");
                        Key(host, Keys.F11); Key(host, Keys.F11);
                        stage++; host.Close();
                        break;
                }
            }
            catch (Exception error) { failure = error; timer.Stop(); host.Close(); }
        };
        timer.Start(); Application.Run(host);
        if (added != null && File.Exists(added)) File.Delete(added);
        if (failure != null) throw failure;
        if (stage != 2) throw new Exception("Window checks did not finish.");
        Console.WriteLine("Shader window: dynamic tree, checkbox binding, file watcher discovery, scene selection without capture restart, close/reopen and shutdown passed.");
    }

    static System.Collections.Generic.IEnumerable<Control> Descendants(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AEther.WinUI;
using AEther.Smoke;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;

internal static partial class Program
{
    static void CheckShaderWindows(string? shaders)
    {
        Exception? failure = null;
        bool finished = false;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            var app = new WindowCheckApplication();
            app.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine($"WinUI unhandled exception: {e.Message}\n{e.Exception}");
                failure = e.Exception; e.Handled = true; app.Exit();
            };
            app.StartChecks = () =>
            {
                var started = 0;
                var host = new MainWindow(() => new SyntheticSource(() => started++), shaders);
                T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                var settings = Field<SettingsPanel>("settings");
                var canvasWindow = Field<CanvasWindow>("canvasWindow");
                var audio = Field<AudioWindow>("audio");
                var debug = Field<DebugWindow>("debug");
                var shaderWindow = Field<ShaderWindow>("shaders");
                Session? original = null;
                Task? restart = null;
                string? editedPath = null;
                PointInt32 canvasPosition = default;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                int stage = 0;
                var deadline = DateTime.UtcNow.AddSeconds(40);
                var added = shaders == null ? null : Path.Combine(shaders, "Scenes", "WatcherProbe.hlsl");
                host.Closed += (_, _) =>
                {
                    timer.Stop();
                    if (added != null && File.Exists(added)) File.Delete(added);
                    finished = stage == 7;
                };
                timer.Tick += (_, _) =>
                {
                    try
                    {
                        if (DateTime.UtcNow > deadline) throw new Exception($"WinUI window check timed out at stage {stage}.");
                        var current = Field<Session?>("session");
                        if (current == null) return;
                        switch (stage)
                        {
                            case 0:
                                original = current;
                                if (host.Content != settings || !host.AppWindow.IsShownInSwitchers
                                    || canvasWindow.AppWindow.IsShownInSwitchers || canvasWindow.Title != ""
                                    || canvasWindow.AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter
                                        { IsResizable: true, HasTitleBar: true, HasBorder: true })
                                    throw new Exception("Hub must be primary; canvas must be untitled, windowed and resizable.");
                                if (((UserControl)canvasWindow.Content).Content is not SwapChainPanel)
                                    throw new Exception("Canvas window must contain only the rendering canvas.");
                                if (((UserControl)canvasWindow.Content).KeyboardAccelerators.Count != 9)
                                    throw new Exception("Render-window shortcuts are missing.");
                                host.Activate();
                                audio.Show(); debug.Show(); shaderWindow.Show();
                                stage++;
                                break;
                            case 1:
                                if (!host.AppWindow.IsVisible) throw new Exception("Settings did not open.");
                                var hub = (StackPanel)((Grid)settings.Content).Children.Single();
                                var buttons = ((StackPanel)hub.Children.Last()).Children.OfType<Button>()
                                    .Select(b => b.Content?.ToString()).ToArray();
                                if (!buttons.SequenceEqual(new[] { "Canvas", "Audio", "Scene", "Debug" })
                                    || Descendants(settings.Content).OfType<ComboBox>().Count() != 2)
                                    throw new Exception("Hub must contain four buttons and the Input/Scene selectors.");
                                if (!audio.IsVisible || !debug.IsVisible)
                                    throw new Exception("Tool windows did not open.");
                                var selector = Descendants(settings.Content).OfType<ComboBox>().Single(c => c.Name == "Scene");
                                if (selector.Header != null || hub.Children[2] != selector
                                    || Descendants(shaderWindow.Content).OfType<ComboBox>().Any())
                                    throw new Exception("Scene picker must follow Gain in the hub, without a visible label.");
                                if (!Descendants(audio.Content).OfType<NumberBox>().Any())
                                    throw new Exception("Analyzer property editor is missing.");
                                if (!Descendants(settings.Content).OfType<ComboBox>().Any(c => c.Header?.ToString() == "Input")
                                    || !Descendants(settings.Content).OfType<NumberBox>().Any(n => n.Header?.ToString() == "Gain")
                                    || Descendants(audio.Content).OfType<NumberBox>().Any(n => n.Header?.ToString() == "Gain"))
                                    throw new Exception("Input and Gain must live in the hub.");
                                shaderWindow.Hide();
                                selector.SelectedItem = "Spectrum";
                                selector.SelectedItem = "Mandelbox";
                                stage++;
                                break;
                            case 2:
                                if (shaderWindow.IsVisible || current.Renderer.SceneName != "Mandelbox")
                                    throw new Exception("Scene selection must update rendering without opening its properties window.");
                                shaderWindow.Show();
                                if (!Descendants(shaderWindow.Content).OfType<Expander>().Any(g => g.Header?.ToString() == "Geometry")
                                    || !Descendants(shaderWindow.Content).OfType<Slider>().Any())
                                    throw new Exception("Dynamic shader parameter tree is missing.");
                                var number = Descendants(shaderWindow.Content).OfType<NumberBox>().First();
                                number.Value = .75;
                                var row = (FrameworkElement)VisualTreeHelper.GetParent(number);
                                editedPath = (string)row.Tag;
                                var values = current.Renderer.ActiveShader!.Parameters!;
                                if (values.GetFloat(Find(values, (string)row.Tag)) != .75f)
                                    throw new Exception("Numeric editor did not update the shader.");
                                NativeClose(shaderWindow);
                                if (shaderWindow.IsVisible) throw new Exception("Closing shader window did not hide it.");
                                shaderWindow.Show();
                                var scenes = Descendants(settings.Content).OfType<ComboBox>().Single(c => c.Name == "Scene");
                                scenes.SelectedItem = "Spectrum";
                                stage++;
                                break;
                            case 3:
                                var checkbox = Descendants(shaderWindow.Content).OfType<CheckBox>().Single(c => c.Content?.ToString() == "ShowS");
                                checkbox.IsChecked = false;
                                var spectrum = current.Renderer.ActiveShader!.Parameters!;
                                if (spectrum.GetBoolean(Find(spectrum, "ShowS")))
                                    throw new Exception("Checkbox did not update the shader.");
                                shaderWindow.Hide();
                                if (added != null) File.WriteAllText(added, "float4 PS(PSin i):SV_TARGET { return float4(0.2,0.3,0.4,1); }");
                                canvasWindow.AppWindow.Resize(new SizeInt32(800, 600));
                                canvasWindow.AppWindow.Move(new PointInt32(canvasWindow.AppWindow.Position.X + 30,
                                    canvasWindow.AppWindow.Position.Y + 30));
                                var areas = Microsoft.UI.Windowing.DisplayArea.FindAll();
                                var currentArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(canvasWindow.AppWindow.Id,
                                    Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                                for (int i = 0; i < areas.Count; i++)
                                {
                                    if (areas[i].DisplayId.Value == currentArea.DisplayId.Value) continue;
                                    canvasWindow.AppWindow.Move(new PointInt32(areas[i].WorkArea.X + 40, areas[i].WorkArea.Y + 40));
                                    break;
                                }
                                canvasPosition = canvasWindow.AppWindow.Position;
                                typeof(MainWindow).GetMethod("ToggleFullscreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null);
                                if (canvasWindow.AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
                                    || host.AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped)
                                    throw new Exception("Fullscreen must affect only the canvas.");
                                typeof(MainWindow).GetMethod("ToggleFullscreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null);
                                stage++;
                                break;
                            case 4:
                                if (added != null && !current.Renderer.Scenes.Any(s => s.Name == "WatcherProbe")) return;
                                if (started != 1 || current != original) throw new Exception("Shader editing restarted audio capture.");
                                if (shaderWindow.IsVisible) throw new Exception("Shader reload reopened a hidden window.");
                                if (added != null)
                                {
                                    var dropdown = Descendants(settings.Content).OfType<ComboBox>().Single(c => c.Name == "Scene");
                                    if (!dropdown.Items.Contains("WatcherProbe")) throw new Exception("Watcher failed to refresh scene UI.");
                                }
                                // This replay uses a logical clock after timestamped live capture.
                                // Stop live rendering so it cannot consume and clear the synthetic
                                // block between our explicit draw and readback. The audio edit
                                // below starts a new render worker through the normal restart path.
                                Field<RenderWorker?>("renderWorker")?.Dispose();
                                typeof(MainWindow).GetField("renderWorker", BindingFlags.Instance | BindingFlags.NonPublic)!
                                    .SetValue(host, null);
                                Seed(current.Renderer, generation: 1);
                                current.Renderer.Render(false);
                                if (!current.Renderer.ReadPixels().Where((_, i) => i % 4 != 3).Any(v => v > 10))
                                    throw new Exception("Composition renderer produced no visible output.");
                                Console.WriteLine($"WinUI composition output: {current.Renderer.OutputDescription}");
                                NativeClose(canvasWindow);
                                if (canvasWindow.IsVisible || !host.AppWindow.IsVisible)
                                    throw new Exception("Closing canvas must leave the hub open.");
                                canvasWindow.Show();
                                host.Activate();
                                foreach (var tool in new ToolWindow[] { audio, debug })
                                {
                                    NativeClose(tool);
                                    if (tool.IsVisible) throw new Exception("Closing a tool window did not hide it.");
                                    tool.Show();
                                }
                                stage++;
                                if (added != null) File.Delete(added);
                                Descendants(settings.Content).OfType<NumberBox>().Single(n => n.Header?.ToString() == "Gain").Value += 1;
                                break;
                            case 5:
                                if (started != 2 || Field<RenderWorker?>("renderWorker") == null) return;
                                if (added != null && current.Renderer.Scenes.Any(s => s.Name == "WatcherProbe")) return;
                                if (added != null && Descendants(settings.Content).OfType<ComboBox>()
                                    .Single(c => c.Name == "Scene").Items.Contains("WatcherProbe"))
                                    throw new Exception("Deleted scene remains in the picker.");
                                if (!ReferenceEquals(current, original))
                                    throw new Exception("Audio edit replaced the graphics session.");
                                if (canvasWindow.AppWindow.Position != canvasPosition || canvasWindow.Title != "")
                                    throw new Exception("Audio edit moved or labeled the canvas.");
                                var retained = current.Renderer.ActiveShader!.Parameters!;
                                if (current.Renderer.SceneName != "Spectrum" || retained.GetBoolean(Find(retained, "ShowS")))
                                    throw new Exception("Audio edit lost shader state.");
                                stage++;
                                restart = (Task)typeof(MainWindow).GetMethod("RestartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null)!;
                                break;
                            case 6:
                                if (restart?.IsCompleted != true) return;
                                restart.GetAwaiter().GetResult();
                                if (canvasWindow.AppWindow.Position != canvasPosition)
                                    throw new Exception("Session restart moved the canvas.");
                                if (started != 3 || ReferenceEquals(current, original) || !original!.Analyzer.Completion.IsCompleted)
                                    throw new Exception("Session restart did not stop the previous analysis worker.");
                                var restored = current.Renderer.ActiveShader!.Parameters!;
                                var restoredPicker = Descendants(settings.Content).OfType<ComboBox>().Single(c => c.Name == "Scene");
                                if (restoredPicker.SelectedItem?.ToString() != current.Renderer.SceneName)
                                    throw new Exception("Session restart did not synchronize the hub scene picker.");
                                if (current.Renderer.SceneName != "Spectrum" || restored.GetBoolean(Find(restored, "ShowS")))
                                    throw new Exception("Session restart lost the selected scene or parameter values.");
                                shaderWindow.SelectScene("Mandelbox");
                                if (restoredPicker.SelectedItem?.ToString() != "Mandelbox")
                                    throw new Exception("Programmatic selection did not synchronize the hub scene picker.");
                                restored = current.Renderer.ActiveShader!.Parameters!;
                                if (restored.GetFloat(Find(restored, editedPath!)) != .75f)
                                    throw new Exception("Session restart lost another scene's parameter values.");
                                debug.Hide();
                                typeof(MainWindow).GetMethod("RenderFailed", BindingFlags.Instance | BindingFlags.NonPublic)!
                                    .Invoke(host, new object[] { new Exception("Window smoke diagnostic") });
                                if (!debug.IsVisible || !Descendants(debug.Content).OfType<TextBlock>()
                                    .Any(t => t.Text == "Window smoke diagnostic"))
                                    throw new Exception("Rendering failures did not open Debug with the error.");
                                stage++;
                                NativeClose(host);
                                break;
                        }
                    }
                    catch (Exception error) { Console.Error.WriteLine(error); failure = error; timer.Stop(); host.RequestClose(); }
                };
                host.Activate();
                timer.Start();
            };
        });
        if (failure != null) throw new Exception("WinUI window checks failed.", failure);
        if (!finished) throw new Exception("WinUI window checks did not finish.");
        Console.WriteLine("WinUI: composition rendering, resize, fullscreen, property editors, live shader edits, watcher, close/reopen, session restart and shutdown passed.");
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    static void NativeClose(Window window) => SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x10, IntPtr.Zero, IntPtr.Zero);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}

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
using AEther.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
                CheckEventSummary();
                var started = 0;
                var host = new MainWindow(() => new SyntheticSource(() => started++), shaders);
                T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                var settings = Field<SettingsPanel>("settings");
                var canvasWindow = Field<CanvasWindow>("canvasWindow");
                var analysis = Field<AnalysisView>("analysis");
                var backlog = Field<FrameTimeView>("frameTime");
                var audio = Field<AudioPanel>("audio");
                var eventLog = settings.EventLog;
                var shaderPanel = Field<ShaderPanel>("shaders");
                var graphics = Field<GraphicsPanel>("graphics");
                bool shadersVisible() => shaderPanel.Visibility == Visibility.Visible;
                Session? original = null;
                Task? restart = null;
                string? editedPath = null;
                PointInt32 canvasPosition = default;
                long debugDraws = 0;
                int hubWidth = 0, hubHeight = 0;
                double inputTop = 0, sceneTop = 0;
                double Top(FrameworkElement element) => element.TransformToVisual(settings).TransformPoint(default).Y;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                int stage = 0;
                var deadline = DateTime.UtcNow.AddSeconds(40);
                var added = shaders == null ? null : Path.Combine(shaders, "Scenes", "WatcherProbe.hlsl");
                host.Closed += (_, _) =>
                {
                    timer.Stop();
                    if (added != null && File.Exists(added)) File.Delete(added);
                    finished = stage == 13;
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
                                var launchScene = Environment.GetCommandLineArgs()
                                    .FirstOrDefault(argument => argument.StartsWith("--scene=", StringComparison.Ordinal))?[8..];
                                if (launchScene != null)
                                {
                                    if (!canvasWindow.IsVisible || current.Renderer.SceneName != launchScene)
                                        throw new Exception("Explicit scene launch did not open the requested canvas.");
                                    NativeClose(canvasWindow);
                                }
                                if (host.SystemBackdrop is not MicaBackdrop || canvasWindow.SystemBackdrop != null
                                    || (host.Content != settings && (host.Content is not Grid hub || !hub.Children.Contains(settings)))
                                    || !host.AppWindow.IsShownInSwitchers
                                    || canvasWindow.AppWindow.IsShownInSwitchers || canvasWindow.Title != ""
                                    || canvasWindow.AppWindow.Presenter is not Microsoft.UI.Windowing.OverlappedPresenter
                                        { IsResizable: true, HasTitleBar: true, HasBorder: true })
                                    throw new Exception("Hub must be primary; canvas must be untitled, windowed and resizable.");
                                if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()
                                    && (Field<TitleBar?>("hubTitleBar") is not { } bar || bar.Title != host.Title || !host.ExtendsContentIntoTitleBar))
                                    throw new Exception("The hub must have an integrated title bar matching the window title.");
                                if (((UserControl)canvasWindow.Content).Content is not SwapChainPanel)
                                    throw new Exception("Canvas window must contain only the rendering canvas.");
                                var shortcuts = ((UserControl)canvasWindow.Content).KeyboardAccelerators;
                                if (shortcuts.Count != 1 || shortcuts[0].Key != VirtualKey.F11)
                                    throw new Exception("The canvas must have only the F11 shortcut.");
                                if (((UserControl)canvasWindow.Content).KeyboardAcceleratorPlacementMode != KeyboardAcceleratorPlacementMode.Hidden
                                    || settings.KeyboardAcceleratorPlacementMode != KeyboardAcceleratorPlacementMode.Hidden
                                    || settings.KeyboardAccelerators.Count != 1 || settings.KeyboardAccelerators[0].Key != VirtualKey.F11)
                                    throw new Exception("Fullscreen shortcuts must remain available without automatic tooltips.");
                                host.Activate();
                                var audioGroups = ((StackPanel)audio.Content).Children.OfType<Expander>().ToArray();
                                if (canvasWindow.IsVisible || shadersVisible() || audioGroups.Any(g => g.IsExpanded))
                                    throw new Exception("Startup must leave canvas, shader settings and audio categories collapsed.");
                                if (!audioGroups.Select(g => g.Header?.ToString()).SequenceEqual(new[] { "DFT", "Pipeline", "Smoother", "Splitter" }))
                                    throw new Exception("Audio categories must follow AnalyzerOptions metadata.");
                                if (Math.Abs(Top(settings.AudioSettings) + settings.AudioSettings.ActualHeight
                                    - (Top(settings.AudioColumn) + settings.AudioColumn.ActualHeight)) > 1
                                    || analysis.ActualHeight <= 0 || Grid.GetRow(analysis) != 0)
                                    throw new Exception("Audio settings and live views must occupy bounded grid rows.");
                                if (LogicalChildren((DependencyObject)audio.Content).OfType<NumberBox>().Any(n => AutomationProperties.GetName(n) == "HistorySeconds"))
                                    throw new Exception("HistorySeconds must not belong to audio settings.");
                                if (!LogicalChildren((DependencyObject)audio.Content).OfType<NumberBox>().Any(n => AutomationProperties.GetName(n) == "Input gain (dB)")
                                    || LogicalChildren((DependencyObject)audio.Content).OfType<NumberBox>().Any(n => AutomationProperties.GetName(n) == "Peak emphasis"))
                                    throw new Exception("Analyzer and graphics options must appear in their respective columns.");
                                if (!LogicalChildren((DependencyObject)graphics.Content).OfType<NumberBox>().Any(n => AutomationProperties.GetName(n) == "HistorySeconds")
                                    || !LogicalChildren((DependencyObject)graphics.Content).OfType<NumberBox>().Any(n => AutomationProperties.GetName(n) == "Peak emphasis")
                                    || graphics.Visibility != Visibility.Visible || Grid.GetRow(graphics) >= Grid.GetRow(shaderPanel)
                                    || !settings.SceneColumn.Children.Contains(graphics))
                                    throw new Exception("HistorySeconds must remain visible above scene-specific controls in the right column.");
                                foreach (var group in audioGroups) group.IsExpanded = true;
                                if (eventLog.IsExpanded) throw new Exception("Event log must start collapsed.");
                                for (int i = 0; i < 24; i++) current.Logger.Information("Window event table history {Index}", i);
                                Task.Run(() => current.Logger.Warning("Window warning smoke")).GetAwaiter().GetResult();
                                eventLog.Refresh();
                                if (!eventLog.StatusSummary.Children.Any(item => AutomationProperties.GetName(item).StartsWith("Warning:")))
                                    throw new Exception("Background warnings must appear in the event counts.");
                                eventLog.Toggle();
                                stage++;
                                break;
                            case 1:
                                if (current.Renderer.DebugDrawCount == 0) return;
                                if (!analysis.CanRender || !Descendants(settings.Content).OfType<SwapChainPanel>().Any()
                                    || eventLog.Events.ActualHeight <= 0 || eventLog.ActualHeight > settings.ActualHeight * .4 + 40
                                    || !Descendants(eventLog.Events).OfType<TextBlock>().Any(t => t.Text.Contains("Window warning smoke")))
                                    throw new Exception("The event log must display real rows in a bounded panel beside the live graphics.");
                                if (eventLog.Events.Columns.Count != 4
                                    || !eventLog.Events.Columns.Select(c => c.Header?.ToString()).SequenceEqual(new[] { "Time", "Severity", "Source", "Message" })
                                    || !Descendants(eventLog.Events).OfType<FontIcon>().Any(icon => AutomationProperties.GetName(icon) == "Warning"))
                                    throw new Exception("Event table must expose four aligned columns and icon-only severity cells.");
                                var latestMessage = Descendants(eventLog.Events).OfType<TextBlock>().First(t => t.Text.Contains("Window warning smoke"));
                                var latestY = latestMessage.TransformToVisual(eventLog.Events).TransformPoint(default).Y;
                                if (latestY < 0 || latestY + latestMessage.ActualHeight > eventLog.Events.ActualHeight)
                                    throw new Exception("Opening a long event table must scroll to the latest event.");
                                if (analysis.ActualHeight <= 0
                                    || Math.Abs(Top(settings.AudioSettings) + settings.AudioSettings.ActualHeight
                                        - (Top(settings.AudioColumn) + settings.AudioColumn.ActualHeight)) > 1)
                                    throw new Exception("Analysis must fill the available space above bottom-aligned audio settings.");
                                if (!host.AppWindow.IsVisible) throw new Exception("Settings did not open.");
                                if (settings.Content is not Grid || Math.Abs(settings.AudioColumn.ActualWidth - settings.SceneColumn.ActualWidth) > 1)
                                    throw new Exception("The hub must have two equal-width columns without an outer scroller.");
                                if (Descendants(settings.Content).OfType<Expander>().Any(e => e.Header?.ToString() == "Debug"))
                                    throw new Exception("Text debug diagnostics must be removed.");
                                var selector = Descendants(settings.Content).OfType<ComboBox>().Single(c => c.Name == "Scene");
                                if (selector.SelectedItem != null || shadersVisible()
                                    || !settings.Columns.Children.Contains(analysis) || settings.SceneColumn.Children[0] is not Grid sceneRow || !sceneRow.Children.Contains(selector))
                                    throw new Exception("Scene must start empty with analysis above the audio controls.");
                                if (!Descendants(audio.Content).OfType<NumberBox>().Any())
                                    throw new Exception("Analyzer property editor is missing.");
                                if (!Descendants(settings.Content).OfType<ComboBox>().Any(c => AutomationProperties.GetName(c) == "Input")
                                    || !Descendants(audio.Content).OfType<NumberBox>().Any(n => AutomationProperties.GetName(n) == "Input gain (dB)"))
                                    throw new Exception("Input and Gain must live in the hub.");
                                selector.SelectedItem = "Spectrum";
                                selector.SelectedItem = "Mandelbox";
                                stage++;
                                break;
                            case 2:
                                if (!shadersVisible() || !canvasWindow.IsVisible || current.Renderer.SceneName != "Mandelbox")
                                    throw new Exception("Scene selection must open canvas and reveal embedded shader settings.");
                                if (current.Analyzer.Progress.Latest.Gpu == null) return;
                                if (!settings.Columns.Children.Contains(backlog) || Grid.GetColumn(backlog) != 2 || Grid.GetRow(backlog) != 0
                                    || !backlog.CurveNames.SequenceEqual(new[] { "Total", "CPU", "GPU", "Input" }))
                                    throw new Exception("The frame-time graph must occupy the right live-view pane.");
                                var shaderGroups = LogicalChildren((DependencyObject)shaderPanel.Content).OfType<Expander>().ToArray();
                                if (!shaderGroups.Any(g => g.Header?.ToString() == "Geometry"))
                                    throw new Exception("Dynamic shader parameter tree is missing.");
                                foreach (var group in shaderGroups) group.IsExpanded = true;
                                if (!Descendants(shaderPanel.Content).OfType<Slider>().Any()) return;
                                var audioScroll = settings.AudioSettings;
                                var shaderScroll = ((Grid)shaderPanel.Content).Children.OfType<ScrollViewer>().Single();
                                if (audioScroll.ViewportHeight <= 32 || audioScroll.ScrollableHeight <= 0
                                    || audioScroll.ViewportHeight > settings.AudioColumn.ActualHeight
                                    || shaderScroll.ViewportHeight > settings.SceneColumn.ActualHeight)
                                    throw new Exception("Only settings panels must scroll within bounded column space.");
                                inputTop = Top((FrameworkElement)settings.AudioColumn.Children[0]);
                                sceneTop = Top((FrameworkElement)settings.SceneColumn.Children[0]);
                                audioScroll.ChangeView(null, audioScroll.ScrollableHeight, null, true);
                                shaderScroll.ChangeView(null, shaderScroll.ScrollableHeight, null, true);
                                var number = Descendants(shaderPanel.Content).OfType<NumberBox>().First();
                                number.Value = .75;
                                var row = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(number));
                                editedPath = (string)row.Tag;
                                var values = current.Renderer.ActiveShader!.Parameters!;
                                var debugValues = current.Renderer.GetSceneParameters("Debug")!;
                                debugValues.SetBoolean(Find(debugValues, "CombinedLevel"), true);
                                if (values.GetFloat(Find(values, (string)row.Tag)) != .75f)
                                    throw new Exception("Numeric editor did not update the shader.");
                                var scenes = Descendants(settings.Content).OfType<ComboBox>().Single(c => c.Name == "Scene");
                                scenes.SelectedItem = "Spectrum";
                                stage++;
                                break;
                            case 3:
                                if (Math.Abs(inputTop - Top((FrameworkElement)settings.AudioColumn.Children[0])) > .5
                                    || Math.Abs(sceneTop - Top((FrameworkElement)settings.SceneColumn.Children[0])) > .5)
                                    throw new Exception("Scrolling settings moved the pinned input or scene picker.");
                                foreach (var group in ((StackPanel)audio.Content).Children.OfType<Expander>())
                                {
                                    group.IsExpanded = false;
                                    group.IsExpanded = true;
                                }
                                if (started != 1) throw new Exception("Expanding Audio restarted capture.");
                                var checkbox = Descendants(shaderPanel.Content).OfType<CheckBox>().Single(c => c.Content?.ToString() == "ShowS");
                                checkbox.IsChecked = false;
                                var spectrum = current.Renderer.ActiveShader!.Parameters!;
                                if (spectrum.GetBoolean(Find(spectrum, "ShowS")))
                                    throw new Exception("Checkbox did not update the shader.");
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
                                if (!shadersVisible()) throw new Exception("Shader reload hid the inline settings.");
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
                                if (Field<bool>("renderCanvas"))
                                    throw new Exception("Closing canvas must stop scheduling scene rendering immediately.");
                                if (Descendants(settings.Content).OfType<ComboBox>().Single(c => c.Name == "Scene").SelectedItem != null)
                                    throw new Exception("Closing canvas must clear scene selection.");
                                host.Activate();
                                current.Logger.Warning("Window warning before collapse");
                                eventLog.Refresh();
                                var countsBeforeCollapse = eventLog.StatusSummary.Children.Select(AutomationProperties.GetName).ToArray();
                                eventLog.Toggle();
                                eventLog.Refresh();
                                if (!countsBeforeCollapse.SequenceEqual(eventLog.StatusSummary.Children.Select(AutomationProperties.GetName)))
                                    throw new Exception("Collapsing the event log changed its severity counts.");
                                eventLog.Toggle();
                                stage++;
                                if (added != null) File.Delete(added);
                                Descendants(settings.Content).OfType<NumberBox>().Single(n => AutomationProperties.GetName(n) == "Input gain (dB)").Value += 1;
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
                                var closedPicker = Descendants(settings.Content).OfType<ComboBox>().Single(c => c.Name == "Scene");
                                if (closedPicker.SelectedItem != null || canvasWindow.IsVisible || shadersVisible())
                                    throw new Exception("Audio edits and watcher reloads must preserve the dismissed canvas.");
                                closedPicker.SelectedItem = "Spectrum";
                                if (!canvasWindow.IsVisible || current.Renderer.SceneName != "Spectrum" || started != 2)
                                    throw new Exception("Reselecting the same scene must reopen canvas without restarting capture.");
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
                                shaderPanel.SelectScene("Mandelbox");
                                if (restoredPicker.SelectedItem?.ToString() != "Mandelbox")
                                    throw new Exception("Programmatic selection did not synchronize the hub scene picker.");
                                restored = current.Renderer.ActiveShader!.Parameters!;
                                if (restored.GetFloat(Find(restored, editedPath!)) != .75f)
                                    throw new Exception("Session restart lost another scene's parameter values.");
                                debugDraws = current.Renderer.DebugDrawCount;
                                canvasWindow.Hide();
                                stage++;
                                break;
                            case 7:
                                if (current.Renderer.DebugDrawCount <= debugDraws) return;
                                if (current.Renderer.SceneName != "Mandelbox" || started != 3)
                                    throw new Exception("Debug-only rendering changed the canvas scene or restarted capture.");
                                if (eventLog.IsExpanded) eventLog.Toggle();
                                debugDraws = current.Renderer.DebugDrawCount;
                                stage++;
                                break;
                            case 8:
                                if (current.Renderer.DebugDrawCount <= debugDraws)
                                    throw new Exception("Closing event history stopped the hub graph.");
                                host.AppWindow.Hide();
                                debugDraws = current.Renderer.DebugDrawCount;
                                stage++;
                                break;
                            case 9:
                                if (current.Renderer.DebugDrawCount != debugDraws)
                                    throw new Exception("The hidden hub continued drawing its graph.");
                                host.Activate(); canvasWindow.Show();
                                stage++;
                                break;
                            case 10:
                                if (current.Renderer.DebugDrawCount <= debugDraws) return;
                                var restoredDebug = current.Renderer.GetSceneParameters("Debug")!;
                                if (!restoredDebug.GetBoolean(Find(restoredDebug, "CombinedLevel")))
                                    throw new Exception("Full session restart lost the embedded Debug parameters.");
                                if (!analysis.CanRender || current.Renderer.DebugOutputError != null)
                                    throw new Exception("The hub analysis graph failed to reconnect after reopen/resize.");
                                hubWidth = host.AppWindow.Size.Width;
                                hubHeight = host.AppWindow.Size.Height;
                                host.AppWindow.Resize(new SizeInt32(hubWidth + 80, hubHeight - 80));
                                hubWidth += 80; hubHeight -= 80;
                                Descendants(audio.Content).OfType<NumberBox>().Single(n => AutomationProperties.GetName(n) == "FrequencyResolution").Value = 48;
                                stage++;
                                break;
                            case 11:
                                if (started != 4 || current.Renderer.Audio.Bands < 300) return;
                                if (current.Renderer.Audio.Bands < 300 || host.AppWindow.Size.Width != hubWidth || host.AppWindow.Size.Height != hubHeight || analysis.ActualHeight > settings.Columns.RowDefinitions[0].ActualHeight + 1)
                                    throw new Exception("More bands must fit the pinned analysis area without resizing the window or adding scrolling.");
                                if (Math.Abs(settings.AudioColumn.ActualWidth - settings.SceneColumn.ActualWidth) > 1)
                                    throw new Exception("Resizing the hub changed the equal column widths.");
                                Descendants(audio.Content).OfType<NumberBox>().Single(n => AutomationProperties.GetName(n) == "FrequencyResolution").Value = 12;
                                stage++;
                                break;
                            case 12:
                                if (started != 5 || current.Renderer.Audio.Bands >= 300) return;
                                if (host.AppWindow.Size.Width != hubWidth)
                                    throw new Exception("Restoring the band count changed the hub width.");
                                if (eventLog.IsExpanded) eventLog.Toggle();
                                typeof(MainWindow).GetMethod("RenderFailed", BindingFlags.Instance | BindingFlags.NonPublic)!
                                    .Invoke(host, new object[] { new Exception("Window smoke diagnostic") });
                                eventLog.Refresh();
                                if (eventLog.IsExpanded || !eventLog.StatusSummary.Children.Any(item => AutomationProperties.GetName(item).StartsWith("Error:"))
                                    || !((IEnumerable<EventLogView.EventRow>)eventLog.Events.ItemsSource).Any(row => row.Message.Contains("Window smoke diagnostic")))
                                    throw new Exception("Rendering failures must be counted and logged without opening the event table.");
                                if (!((IEnumerable<EventLogView.EventRow>)eventLog.Events.ItemsSource).Any(row => row.Message.Contains("Window warning smoke")))
                                    throw new Exception("Restarts must preserve earlier event history.");
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

    static void CheckEventSummary()
    {
        var store = new EventLogStore(5);
        using var log = new Serilog.LoggerConfiguration().WriteTo.Sink(store).CreateLogger();
        var view = new EventLogView(store);
        view.Refresh();
        if (view.StatusSummary.Children.Count != 0)
            throw new Exception("An empty event history must have an empty status summary.");
        log.Information("Info");
        log.Warning("First warning");
        log.Warning("Second warning");
        log.Error("Error");
        log.Fatal("Fatal");
        view.Refresh();
        var pairs = view.StatusSummary.Children.Cast<StackPanel>().ToArray();
        if (!pairs.Select(AutomationProperties.GetName).SequenceEqual(new[] { "Fatal: 1", "Error: 1", "Warning: 2", "Information: 1" })
            || pairs.Any(pair => pair.Children.Count != 2 || pair.Children[0] is not FontIcon || pair.Children[1] is not TextBlock)
            || !pairs.Select(pair => ((TextBlock)pair.Children[1]).Text).SequenceEqual(new[] { "1", "1", "2", "1" }))
            throw new Exception("The status bar must show one icon and count per retained severity.");
        log.Information("New info 1");
        log.Information("New info 2");
        log.Information("New info 3");
        view.Refresh();
        if (!view.StatusSummary.Children.Select(AutomationProperties.GetName).SequenceEqual(new[] { "Fatal: 1", "Error: 1", "Information: 3" }))
            throw new Exception("Severity counts must follow history eviction and omit empty categories.");
    }

    static void NativeClose(Window window) => SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x10, IntPtr.Zero, IntPtr.Zero);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}


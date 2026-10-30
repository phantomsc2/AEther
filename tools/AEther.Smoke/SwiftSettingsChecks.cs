using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using AEther.Smoke;
using AEther.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

internal static partial class Program
{
    static void ResetParameter(FrameworkElement row)
    {
        var item = ((MenuFlyout)row.ContextFlyout).Items.OfType<MenuFlyoutItem>().Single();
        if (item.Text != "Reset to default" || item.Icon is not SymbolIcon)
            throw new Exception("Parameters must expose the native reset command with an icon.");
        var peer = new MenuFlyoutItemAutomationPeer(item);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    sealed class CategoryProbeOptions
    {
        [Category("Zebra")] public bool Grouped { get; set; }
        public bool Plain { get; set; }
        [Category("Misc")] public bool ExplicitMisc { get; set; }
        [Category("")] public bool EmptyCategory { get; set; }
        [Category("Alpha")] public bool OtherGroup { get; set; }
        public CategoryProbeOptions Copy() => (CategoryProbeOptions)MemberwiseClone();
    }

    // Interactive pointer probe: the title counts actual pipeline-setting commits.
    // Dragging should change the NumberBox continuously and increment the title once.
    static void CheckSliderPointerEdits()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread()));
            var app = new WindowCheckApplication();
            app.StartChecks = () =>
            {
                var settings = new AudioPanel();
                var host = new AudioProbeWindow(settings);
                int commits = 0;
                settings.SettingsChanged += (_, _) =>
                {
                    host.Title = $"Slider commits: {++commits}";
                    Console.WriteLine($"Commit {commits}: min frequency {settings.Options.MinFrequency:G17}");
                };
                host.AppWindow.Closing += (_, _) => app.Exit();
                host.Activate();
            };
        });
    }

    sealed class AudioProbeWindow : ToolWindow
    {
        public AudioProbeWindow(AudioPanel panel) : base("Slider commits: 0", 480, 840) =>
            Layout.Children.Add(new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }

    static void CheckSwiftSettings()
    {
        Exception? failure = null;
        bool finished = false;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread()));
            var app = new WindowCheckApplication();
            app.StartChecks = () =>
            {
                try
                {
                    var categoryEditor = new OptionsEditor<CategoryProbeOptions>(new(), value => value.Copy(), new());
                    var categoryChildren = categoryEditor.Content.Children.ToArray();
                    var topLevel = categoryChildren.Take(2).Select(AutomationProperties.GetName).ToArray();
                    if (!topLevel.ToHashSet().SetEquals(new[] { "Plain", "ExplicitMisc" }))
                        throw new Exception("Default and explicit Misc properties must be top-level controls.");
                    var categoryGroups = categoryChildren.Skip(2).OfType<Expander>().ToArray();
                    if (categoryGroups.Length != 3 || categoryGroups.Any(e => e.IsExpanded)
                        || !categoryGroups.Select(e => e.Header?.ToString()).SequenceEqual(new[] { "", "Alpha", "Zebra" }))
                        throw new Exception("Empty and named categories must remain separate collapsed groups.");
                    var topToggle = LogicalChildren(categoryChildren.Single(c => AutomationProperties.GetName(c) == "Plain"))
                        .OfType<ToggleSwitch>().Single();
                    int categoryChanges = 0;
                    categoryEditor.Changed += (_, _) => categoryChanges++;
                    topToggle.IsOn = true;
                    if (!categoryEditor.Options.Plain || categoryChanges != 1)
                        throw new Exception("A top-level control must update its option normally.");
                    // Construct the real controls without activating or displaying a window.
                    var settings = new AudioPanel();
                    var restored = new AudioPanel(new AEther.AnalyzerOptions { SwiftWidthScale = 2 });
                    var restoredWidth = LogicalChildren((DependencyObject)restored.Content).OfType<NumberBox>()
                        .Single(n => AutomationProperties.GetName(n) == "SWIFT width (octaves)");
                    if (restoredWidth.Value != 1 || restored.Options.SwiftWidthScale != 2)
                        throw new Exception("Restored analyzer settings were not reflected in the editor.");
                    var restoredRow = LogicalChildren((DependencyObject)restored.Content).OfType<Grid>()
                        .Single(g => g.Tag as string == "SwiftWidthOctaves");
                    int restoredChanges = 0;
                    restored.SettingsChanged += (_, _) => restoredChanges++;
                    ResetParameter(restoredRow);
                    if (restoredWidth.Value != 0 || restored.Options.SwiftWidthScale != 1 || restoredChanges != 1)
                        throw new Exception("Reset must restore the declared default, not the loaded session value.");
                    ResetParameter(restoredRow);
                    if (restoredChanges != 1) throw new Exception("Resetting an unchanged value must not publish an edit.");
                    var elements = LogicalChildren((DependencyObject)settings.Content).ToArray();
                    var graphics = new GraphicsPanel();
                    var graphicsRows = ((StackPanel)graphics.Content).Children.ToArray();
                    if (graphicsRows.Length != 3 || graphicsRows.Any(row => row is Expander)
                        || !graphicsRows.Select(AutomationProperties.GetName).ToHashSet().SetEquals(new[] { "HistorySeconds", "Peak emphasis", "Exposure" }))
                        throw new Exception("Graphics options must be top-level controls.");
                    var graphicsElements = LogicalChildren((DependencyObject)graphics.Content).ToArray();
                    var history = graphicsElements.OfType<NumberBox>().Single(n => AutomationProperties.GetName(n) == "HistorySeconds");
                    var emphasis = graphicsElements.OfType<NumberBox>().Single(n => AutomationProperties.GetName(n) == "Peak emphasis");
                    int graphicsChanges = 0;
                    graphics.SettingsChanged += (_, _) => graphicsChanges++;
                    history.Value = 3;
                    emphasis.Value = .75;
                    if (graphics.Options.HistorySeconds != 3 || graphics.Options.SpectrumAggregation != .75 || graphicsChanges != 2)
                        throw new Exception("Generated graphics editors did not publish options.");
                    graphics.SetOptions(new AEther.Vortice.GraphicsOptions(4, .25));
                    if (history.Value != 4 || emphasis.Value != .25 || graphicsChanges != 2)
                        throw new Exception("Graphics option replacement did not refresh the editors.");
                    var numbers = elements.OfType<NumberBox>().ToArray();
                    var width = numbers.Single(n => AutomationProperties.GetName(n) == "SWIFT width (octaves)");
                    var imbalance = numbers.Single(n => AutomationProperties.GetName(n) == "SWIFT imbalance");
                    Grid Controls(NumberBox n) => elements.OfType<Grid>().Single(p => p.Children.Contains(n));
                    Slider SliderFor(NumberBox n) => Controls(n).Children.OfType<Slider>().Single();
                    var widthSlider = SliderFor(width);
                    var dSlider = SliderFor(imbalance);
                    var rate = numbers.Single(n => AutomationProperties.GetName(n) == "TimeResolution");
                    var windowSeconds = numbers.Single(n => AutomationProperties.GetName(n) == "TimeWindowSeconds");
                    rate.Value = 500;
                    if (windowSeconds.Minimum != 0)
                        throw new Exception("Numeric bounds must come directly from Range metadata.");
                    void Equal(double actual, double expected)
                    {
                        if (Math.Abs(actual - expected) > 1e-10)
                            throw new Exception($"SWIFT settings: expected {expected}, got {actual}.");
                    }
                    Equal(width.Minimum, -2); Equal(width.Maximum, 3); Equal(width.Value, 0);
                    Equal(widthSlider.Minimum, -2); Equal(widthSlider.Maximum, 3); Equal(widthSlider.Value, 0);
                    Equal(imbalance.Minimum, 0); Equal(imbalance.Maximum, 1); Equal(imbalance.Value, 0);
                    foreach (var value in new[] { .25, 1.0, 2.0, 8.0 })
                    {
                        width.Value = Math.Log2(value);
                        Equal(settings.Options.SwiftWidthScale, value);
                        Equal(widthSlider.Value, Math.Log2(value));
                        if (widthSlider.ThumbToolTipValueConverter != null)
                            throw new Exception("Slider must not know about option value transformations.");
                    }
                    widthSlider.Value = -1;
                    Equal(width.Value, -1); Equal(settings.Options.SwiftWidthScale, .5);
                    foreach (var value in new[] { 0.0, .5, 1.0 })
                    {
                        dSlider.Value = value;
                        Equal(imbalance.Value, value); Equal(settings.Options.SwiftImbalance, value);
                    }
                    var logAcceleration = numbers.Single(n => AutomationProperties.GetName(n) == "Log acceleration intensity (log2 control)");
                    var complexAcceleration = numbers.Single(n => AutomationProperties.GetName(n) == "Complex acceleration intensity (log2 control)");
                    var noise = numbers.Single(n => AutomationProperties.GetName(n) == "Measurement noise RMS (log2)");
                    logAcceleration.Value = 0;
                    complexAcceleration.Value = 0;
                    Equal(settings.Options.LogAccelerationIntensity, 0);
                    Equal(settings.Options.ComplexAccelerationIntensity, 0);
                    logAcceleration.Value = 1;
                    complexAcceleration.Value = 1;
                    Equal(settings.Options.LogAccelerationIntensity / 1e-8, 1);
                    Equal(settings.Options.ComplexAccelerationIntensity / 1e-8, 1);
                    noise.Value = -10;
                    Equal(settings.Options.SmoothingNoiseFloor, 1.0 / 1024);
                    if (((StackPanel)settings.Content).Children.FirstOrDefault() is not FrameworkElement gain
                        || AutomationProperties.GetName(gain) != "Input gain (dB)")
                        throw new Exception("Input gain must be a top-level control above the analyzer categories.");
                    var toggle = elements.OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == "Spectrum analysis");
                    var window = elements.OfType<ComboBox>().Single(c => AutomationProperties.GetName(c) == "Window function");
                    toggle.IsOn = false;
                    if (settings.Options.UseSwift || !width.IsEnabled || !window.IsEnabled)
                        throw new Exception("Backend selection must update the option without special control handling.");
                    toggle.IsOn = true;
                    if (!settings.Options.UseSwift)
                        throw new Exception("Backend toggle did not update its option.");
                    toggle.IsOn = false;
                    ResetParameter(elements.OfType<Grid>().Single(g => g.Tag as string == "UseSwift"));
                    if (!settings.Options.UseSwift || !toggle.IsOn)
                        throw new Exception("Reset must synchronize boolean options and controls.");
                    window.SelectedIndex = (window.SelectedIndex + 1) % window.Items.Count;
                    ResetParameter(elements.OfType<Grid>().Single(g => g.Tag as string == "WindowFunction"));
                    if (settings.Options.WindowFunction != new AEther.AnalyzerOptions().WindowFunction
                        || ((ComboBoxItem)window.SelectedItem).Tag is not AEther.DFTWindow.Hann)
                        throw new Exception("Reset must synchronize enum options and controls.");
                    finished = true;
                }
                catch (Exception error) { failure = error; }
                finally { app.Exit(); }
            };
        });
        if (failure != null) throw new Exception("SWIFT settings checks failed.", failure);
        if (!finished) throw new Exception("SWIFT settings checks did not finish.");
        Console.WriteLine("Generated settings: defaults, ranges, numeric sync, option-owned conversions, graphics edits and backend selection passed.");
    }

    static IEnumerable<DependencyObject> LogicalChildren(DependencyObject root)
    {
        yield return root;
        IEnumerable<DependencyObject> children = root switch
        {
            Panel panel => panel.Children.Cast<DependencyObject>(),
            ContentControl { Content: DependencyObject content } => new[] { content },
            _ => Array.Empty<DependencyObject>(),
        };
        foreach (var child in children)
            foreach (var descendant in LogicalChildren(child)) yield return descendant;
    }
}

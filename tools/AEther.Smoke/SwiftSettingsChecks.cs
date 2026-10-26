using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using AEther.Smoke;
using AEther.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

internal static partial class Program
{
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
                host.ShortcutRequested += _ => { host.Destroy(); app.Exit(); };
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
                    // Construct the real controls without activating or displaying a window.
                    var settings = new AudioPanel();
                    var elements = LogicalChildren((DependencyObject)settings.Content).ToArray();
                    var numbers = elements.OfType<NumberBox>().ToArray();
                    var width = numbers.Single(n => n.Header?.ToString() == "SWIFT width");
                    var imbalance = numbers.Single(n => n.Header?.ToString() == "SWIFT imbalance");
                    StackPanel Row(NumberBox n) => elements.OfType<StackPanel>().Single(p => p.Children.Contains(n));
                    Slider SliderFor(NumberBox n) => Row(n).Children.OfType<Slider>().Single();
                    ContentControl Container(NumberBox n) => elements.OfType<ContentControl>().Single(c => ReferenceEquals(c.Content, Row(n)));
                    var widthSlider = SliderFor(width);
                    var dSlider = SliderFor(imbalance);
                    void Equal(double actual, double expected)
                    {
                        if (Math.Abs(actual - expected) > 1e-10)
                            throw new Exception($"SWIFT settings: expected {expected}, got {actual}.");
                    }
                    Equal(width.Minimum, .25); Equal(width.Maximum, 8); Equal(width.Value, 1);
                    Equal(widthSlider.Minimum, -2); Equal(widthSlider.Maximum, 3); Equal(widthSlider.Value, 0);
                    Equal(imbalance.Minimum, 0); Equal(imbalance.Maximum, 1); Equal(imbalance.Value, 0);
                    Equal(dSlider.StepFrequency, .01);
                    foreach (var value in new[] { .25, 1.0, 2.0, 8.0 })
                    {
                        width.Value = value;
                        Equal(settings.Options.SwiftWidthScale, value);
                        Equal(widthSlider.Value, Math.Log2(value));
                        var tooltip = widthSlider.ThumbToolTipValueConverter.Convert(widthSlider.Value, typeof(string), null!, "en-US");
                        Equal(double.Parse(tooltip.ToString()!, CultureInfo.CurrentCulture), value);
                    }
                    widthSlider.Value = -1;
                    Equal(width.Value, .5); Equal(settings.Options.SwiftWidthScale, .5);
                    foreach (var value in new[] { 0.0, .5, 1.0 })
                    {
                        dSlider.Value = value;
                        Equal(imbalance.Value, value); Equal(settings.Options.SwiftImbalance, value);
                    }
                    var toggle = elements.OfType<ToggleSwitch>().Single(t => t.Header?.ToString() == "Spectrum analysis");
                    var window = elements.OfType<ComboBox>().Single(c => c.Header?.ToString() == "Window function");
                    var windowContainer = elements.OfType<ContentControl>().Single(c => ReferenceEquals(c.Content, window));
                    toggle.IsOn = false;
                    if (Container(width).IsEnabled || Container(imbalance).IsEnabled || !windowContainer.IsEnabled)
                        throw new Exception("SWIFT controls must be disabled and DFT window enabled in DFT mode.");
                    toggle.IsOn = true;
                    if (!Container(width).IsEnabled || !Container(imbalance).IsEnabled || windowContainer.IsEnabled)
                        throw new Exception("SWIFT controls must be enabled and DFT window disabled in SWIFT mode.");
                    finished = true;
                }
                catch (Exception error) { failure = error; }
                finally { app.Exit(); }
            };
        });
        if (failure != null) throw new Exception("SWIFT settings checks failed.", failure);
        if (!finished) throw new Exception("SWIFT settings checks did not finish.");
        Console.WriteLine("SWIFT settings: defaults, slider endpoints, logarithmic width, numeric sync, physical tooltip and backend enablement passed.");
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

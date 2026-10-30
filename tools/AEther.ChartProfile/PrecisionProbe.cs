using System.ComponentModel.DataAnnotations;
using AEther;
using AEther.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Windows.Globalization.NumberFormatting;

namespace AEther.ChartProfile;

internal sealed class PrecisionProbeOptions
{
    [Range(0.0, 1.0)] public double DefaultValue { get; set; } = .3125;
    [Range(0.0, 1.0), NumericPrecision(5)] public double PreciseValue { get; set; } = .123456;
    [Range(0, 10)] public int IntegerValue { get; set; } = 2;
    public PrecisionProbeOptions Copy() => (PrecisionProbeOptions)MemberwiseClone();
}

internal static class PrecisionProbe
{
    internal static void Start(Application app)
    {
        var editor = new OptionsEditor<PrecisionProbeOptions>(new(), v => v.Copy(), new());
        var root = new StackPanel();
        root.Children.Add(editor.Content);
        var other = new Button { Content = "Other" };
        root.Children.Add(other);
        var window = new Window { Content = root };
        var controls = editor.Content.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Grid>()).SelectMany(g => g.Children.OfType<NumberBox>()).ToArray();
        var standard = controls.Single(n => AutomationProperties.GetName(n) == "DefaultValue");
        var precise = controls.Single(n => AutomationProperties.GetName(n) == "PreciseValue");
        var integer = controls.Single(n => AutomationProperties.GetName(n) == "IntegerValue");
        int changes = 0, phase = 0;
        editor.Changed += (_, _) => changes++;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            try
            {
                switch (phase++)
                {
                    case 0:
                        if (((DecimalFormatter)standard.NumberFormatter).FractionDigits != 3
                            || ((DecimalFormatter)precise.NumberFormatter).FractionDigits != 5
                            || ((DecimalFormatter)integer.NumberFormatter).FractionDigits != 0)
                            throw new Exception("Numeric precision settings do not match metadata.");
                        if (!standard.Focus(FocusState.Programmatic)) throw new Exception("NumberBox could not receive focus.");
                        break;
                    case 1: other.Focus(FocusState.Programmatic); break;
                    case 2:
                        if (editor.Options.DefaultValue != .3125 || changes != 0)
                            throw new Exception($"Focus changed stored value: {editor.Options.DefaultValue:G17}, events {changes}.");
                        if (!standard.Focus(FocusState.Programmatic)) throw new Exception("NumberBox could not receive focus.");
                        standard.Text = 0.4567.ToString(System.Globalization.CultureInfo.CurrentCulture);
                        break;
                    case 3: other.Focus(FocusState.Programmatic); break;
                    case 4:
                        if (Math.Abs(editor.Options.DefaultValue - .4567) > 1e-10 || changes != 1)
                            throw new Exception($"Deliberate edit failed: {editor.Options.DefaultValue:G17}, events {changes}.");
                        editor.SetOptions(new() { DefaultValue = .00004 });
                        if (editor.Options.DefaultValue != .00004 || standard.Value != .00004 || changes != 1)
                            throw new Exception("Refresh changed a small stored value.");
                        if (!standard.Focus(FocusState.Programmatic)) throw new Exception("Small-value editor could not receive focus.");
                        break;
                    case 5: other.Focus(FocusState.Programmatic); break;
                    case 6:
                        if (editor.Options.DefaultValue != .00004 || changes != 1)
                            throw new Exception("Focus rounded the small stored value.");
                        foreach (var locale in new[] { ("en-US", "US", "."), ("de-DE", "DE", ",") })
                        {
                            var formatter = new DecimalFormatter(new[] { locale.Item1 }, locale.Item2) { FractionDigits = 3 };
                            string text = formatter.FormatDouble(1.25);
                            if (!text.Contains(locale.Item3) || Math.Abs(formatter.ParseDouble(text)!.Value - 1.25) > 1e-10)
                                throw new Exception($"Locale formatting failed: {locale.Item1}: {text}");
                        }
                        timer.Stop(); window.Close(); app.Exit(); break;
                }
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine("artifacts", "precision-probe-error.txt"), e.ToString());
                Environment.ExitCode = 1;
                timer.Stop(); window.Close(); app.Exit();
            }
        };
        Directory.CreateDirectory("artifacts");
        window.Activate();
        timer.Start();
    }
}

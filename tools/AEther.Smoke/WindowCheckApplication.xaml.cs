using System;
using Microsoft.UI.Xaml;

namespace AEther.Smoke;

public partial class WindowCheckApplication : Application
{
    public Action? StartChecks { get; set; }
    public WindowCheckApplication() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { StartChecks!(); }
        catch (Exception error) { Console.Error.WriteLine(error); throw; }
    }
}

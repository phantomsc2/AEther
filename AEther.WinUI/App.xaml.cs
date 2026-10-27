using Microsoft.UI.Xaml;
using AEther.Logging;
using Serilog;

namespace AEther.WinUI;

public partial class App : Application
{
    MainWindow? window;

    public App()
    {
        ApplicationLog.Initialize();
        UnhandledException += (_, e) => ApplicationLog.Fatal("WinUI unhandled exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ApplicationLog.Fatal("AppDomain unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "Unobserved task exception");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ApplicationLog.Shutdown();
        InitializeComponent();
        Log.ForContext<App>().Information("WinUI assembly version {WinUIVersion}; working directory {WorkingDirectory}",
            typeof(Application).Assembly.GetName().Version?.ToString(), Environment.CurrentDirectory);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Closed += (_, _) => ApplicationLog.Shutdown();
        window.Activate();
    }
}

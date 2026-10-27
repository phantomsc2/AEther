using Microsoft.UI.Xaml;

namespace AEther.WinUI;

public partial class App : Application
{
    MainWindow? window;

    public App()
    {
        CrashLog.Initialize();
        UnhandledException += (_, e) => CrashLog.Write($"WinUI unhandled exception: {e.Message}; handled={e.Handled}", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write($"AppDomain unhandled exception; terminating={e.IsTerminating}; {e.ExceptionObject}", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => CrashLog.Write("Unobserved task exception", e.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CrashLog.Write("Process exit");
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Closed += (_, _) => CrashLog.Write("Main window closed");
        window.Activate();
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AEther.WinUI;

// Best-effort synchronous writes: the process may terminate as soon as a handler returns.
internal static class CrashLog
{
    static readonly object gate = new();
    static string? path;

    internal static void Initialize()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AEther", "logs");
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            Write($"Started; process={Environment.ProcessPath}; cwd={Environment.CurrentDirectory}; "
                + $".NET={RuntimeInformation.FrameworkDescription}; OS={RuntimeInformation.OSDescription}; "
                + $"architecture={RuntimeInformation.ProcessArchitecture}; debugger={Debugger.IsAttached}; "
                + $"WinUI={typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version}; "
                + $"app={Assembly.GetExecutingAssembly().GetName().Version}");
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (module.ModuleName.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase))
                    Write($"Native WinUI={module.FileVersionInfo.FileVersion}; path={module.FileName}");
        }
        catch { /* Diagnostics must never prevent startup or replace the original failure. */ }
    }

    internal static void Write(string message, Exception? error = null)
    {
        try
        {
            lock (gate)
            {
                if (path == null) return;
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} [thread {Environment.CurrentManagedThreadId}] {message}\n"
                    + (error == null ? "" : $"HRESULT=0x{error.HResult:X8}\n{error}\n"));
            }
        }
        catch { /* The original exception remains unhandled even if logging fails. */ }
    }
}

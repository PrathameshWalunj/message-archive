using System.Windows;

namespace MessageArchive;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (s, args) => LogError(args.ExceptionObject?.ToString() ?? "Unknown error");
        this.DispatcherUnhandledException += (s, args) => { LogError(args.Exception?.ToString() ?? "Unknown dispatcher error"); args.Handled = false; };
    }

    private void LogError(string message)
    {
        try { System.IO.File.AppendAllText("startup_error.txt", $"\n[{DateTime.Now}] {message}\n"); } catch { }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Threading;
using NewsDesk.App.ViewModels;

namespace NewsDesk.App;

public partial class App : System.Windows.Application
{
    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // A live show must not end because of one bad file or a slip in a handler:
        // log the error, tell the operator, keep the studio running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            ErrorLog.Write("Background task", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex) ErrorLog.Write("Fatal", ex);
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("UI", e.Exception);
        e.Handled = true;

        if (MainWindow?.DataContext is MainViewModel vm)
        {
            vm.StatusNotification = $"Recovered from an error ({e.Exception.GetType().Name}: {e.Exception.Message}) — the show continues. Details in {ErrorLog.FilePath}";
        }
    }
}

/// <summary>Appends errors to %LocalAppData%\NewsDesk\Logs\errors.log for later inspection.</summary>
public static class ErrorLog
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NewsDesk", "Logs", "errors.log");

    public static void Write(string source, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never become a second failure
        }
    }
}

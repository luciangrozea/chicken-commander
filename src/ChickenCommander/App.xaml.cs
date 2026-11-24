using System.Windows;
using System.Windows.Threading;

namespace ChickenCommander;

public partial class App : Application
{
    /// <summary>Unexpected errors are appended here, so a crash can be diagnosed afterwards.</summary>
    public static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChickenCommander", "errors.log");

    public App()
    {
        // Last line of defence: an unexpected error in one command is reported instead of closing the program
        // (and losing the open tabs). The details help when reporting the problem.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        // Errors on other threads cannot be caught; at least leave a trace of what happened.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Fatal error", e.ExceptionObject as Exception);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Unexpected error", e.Exception);
        if (e.Exception is StackOverflowException) return;
        e.Handled = true;
        string text;
        if (e.Exception is OutOfMemoryException or InsufficientExecutionStackException)
        {
            // Usually one huge allocation failed (a giant file); the memory it wanted was never taken, so the program can go on.
            GC.Collect();
            text = "There was not enough memory for the last action (probably a very large file).\n\nChicken Commander can keep running.";
        }
        else
        {
            text = $"Something went wrong, but Chicken Commander can keep running.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}";
        }
        var owner = Current?.MainWindow is { IsLoaded: true } w ? w : null;
        if (owner is not null) MessageBox.Show(owner, text, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(text, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Records a problem that did not throw in this process (for example, a crashed helper process).</summary>
    public static void LogError(string what, string detail) => Log(what, detail);

    private static void Log(string what, object? detail)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            var info = new FileInfo(CrashLogPath);
            if (info.Exists && info.Length > 1024 * 1024) info.Delete(); // keep it small
            File.AppendAllText(CrashLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what} (version {typeof(App).Assembly.GetName().Version})\n{detail}\n\n");
        }
        catch (Exception)
        {
            // Logging must never cause another error.
        }
    }
}

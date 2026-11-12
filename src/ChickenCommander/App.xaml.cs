using System.Windows;
using System.Windows.Threading;

namespace ChickenCommander;

public partial class App : Application
{
    public App()
    {
        // Last line of defence: an unexpected error in one command is reported instead of closing the program
        // (and losing the open tabs). The details help when reporting the problem.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is OutOfMemoryException or StackOverflowException) return;
        e.Handled = true;
        var owner = Current?.MainWindow is { IsLoaded: true } w ? w : null;
        var text = $"Something went wrong, but Chicken Commander can keep running.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}";
        if (owner is not null) MessageBox.Show(owner, text, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(text, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}

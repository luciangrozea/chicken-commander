using ChickenCommander.Services;

namespace ChickenCommander;

/// <summary>
/// Entry point. Normally starts the app; with <c>--shell-helper</c> it runs only the Explorer menu (or Properties,
/// Open with) for the main window, in this separate process, so a crashing shell extension cannot take the app down.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == ShellHelperProcess.Argument)
        {
            try
            {
                return ShellHelperProcess.Run(args);
            }
            catch (Exception)
            {
                return 3;
            }
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ChickenCommander.Services;

/// <summary>
/// Runs the Windows Explorer menu, "Properties" and "Open with" in a second, hidden copy of Chicken Commander.
/// <para>
/// These load every shell extension installed on the PC (antivirus, archivers, cloud sync, graphics drivers…)
/// as native code into the calling process. One broken extension can crash or freeze that process, and .NET
/// cannot catch such a crash. Running them in a helper process means the worst case is a message
/// ("the Explorer menu could not be shown") while the panels, tabs and running copies carry on.
/// </para>
/// Protocol: the helper reads the paths from a UTF-8 file (one per line) and writes lines to its output:
/// <c>SHOWN</c> when the menu is on screen, <c>VERB copy</c> for a command the app runs itself,
/// <c>INVOKED</c> after the shell ran a command, <c>NOMENU</c> when Windows built no menu.
/// </summary>
public static class ShellHelperProcess
{
    public const string Argument = "--shell-helper";

    /// <summary>How long Windows may take to build the menu before the helper is considered stuck.</summary>
    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(20);

    // ---- Main process side ------------------------------------------------------------------

    public enum Outcome { Done, NoMenu, Crashed, TimedOut, NotStarted }

    /// <summary>
    /// Shows the Explorer menu for <paramref name="paths"/> at a screen point (physical pixels).
    /// <paramref name="onVerb"/> gets the commands the app runs itself (copy, cut, paste, delete, rename, open);
    /// <paramref name="onShellCommand"/> is called after the shell ran one (files may have changed).
    /// </summary>
    public static Task<(Outcome Outcome, string Detail)> ShowMenuAsync(IntPtr owner, IReadOnlyList<string> paths, Point screenPoint, bool extended,
        Action<string> onVerb, Action onShellCommand, Action onShown) =>
        RunAsync(paths, ["menu", ((int)screenPoint.X).ToString(), ((int)screenPoint.Y).ToString(), extended ? "1" : "0", owner.ToInt64().ToString()],
            onVerb, onShellCommand, onShown);

    /// <summary>Runs one shell verb ("properties", "openas") on the items in the helper process.</summary>
    public static Task<(Outcome Outcome, string Detail)> InvokeAsync(IReadOnlyList<string> paths, string verb, Action onShellCommand) =>
        RunAsync(paths, ["verb", verb], _ => { }, onShellCommand, () => { });

    private static async Task<(Outcome, string)> RunAsync(IReadOnlyList<string> paths, string[] args,
        Action<string> onVerb, Action onShellCommand, Action onShown)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || paths.Count == 0) return (Outcome.NotStarted, "The program path is unknown.");
        var listFile = Path.Combine(Path.GetTempPath(), $"ChickenCommander-menu-{Guid.NewGuid():N}.txt");
        var dispatcher = Dispatcher.CurrentDispatcher;
        Process? process = null;
        try
        {
            await File.WriteAllLinesAsync(listFile, paths, new UTF8Encoding(false));
            var info = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            info.ArgumentList.Add(Argument);
            info.ArgumentList.Add(args[0]);
            info.ArgumentList.Add(listFile);
            foreach (var a in args.Skip(1)) info.ArgumentList.Add(a);

            process = Process.Start(info);
            if (process is null) return (Outcome.NotStarted, "Windows did not start the helper.");
            AllowSetForegroundWindow(process.Id); // the helper's menu must be able to take the focus

            var shown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var noMenu = false;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not { } line) return;
                if (line == "SHOWN") { shown.TrySetResult(true); dispatcher.BeginInvoke(onShown); }
                else if (line == "NOMENU") { noMenu = true; shown.TrySetResult(false); }
                else if (line == "INVOKED") dispatcher.BeginInvoke(onShellCommand);
                else if (line.StartsWith("VERB ", StringComparison.Ordinal)) dispatcher.BeginInvoke(() => onVerb(line[5..]));
            };
            process.BeginOutputReadLine();

            var exited = process.WaitForExitAsync();
            if (args[0] == "menu")
            {
                // A stuck shell extension would otherwise leave the user waiting forever.
                var first = await Task.WhenAny(shown.Task, exited, Task.Delay(MenuTimeout));
                if (first != shown.Task && first != exited)
                {
                    TryKill(process);
                    return (Outcome.TimedOut, $"Windows took more than {MenuTimeout.TotalSeconds:0} seconds to build the menu.");
                }
            }
            await exited;
            process.WaitForExit(); // flushes the remaining output lines
            if (process.ExitCode != 0)
                return (Outcome.Crashed, $"exit code 0x{process.ExitCode:X8}");
            return (noMenu ? Outcome.NoMenu : Outcome.Done, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (process is not null) TryKill(process);
            return (Outcome.NotStarted, ex.Message);
        }
        finally
        {
            process?.Dispose();
            try { File.Delete(listFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void TryKill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    // ---- Helper process side ----------------------------------------------------------------

    /// <summary>Entry point of the helper (from <see cref="Program.Main"/>). Returns the exit code.</summary>
    public static int Run(string[] args)
    {
        // args: --shell-helper menu <list> <x> <y> <extended> <owner hwnd>   |   --shell-helper verb <list> <verb>
        if (args.Length < 4) return 2;
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: coordinates match the main window's
        var paths = File.ReadAllLines(args[2], Encoding.UTF8).Where(p => p.Length > 0).ToList();
        if (paths.Count == 0) return 2;

        // A hidden window owns the menu and receives its messages (Send to, Open with submenus).
        using var source = new HwndSource(new HwndSourceParameters("Chicken Commander shell menu")
        {
            Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 0x80, // WS_POPUP, WS_EX_TOOLWINDOW
        });
        var output = Console.Out;
        bool invoked;
        if (args[1] == "menu" && args.Length >= 7)
        {
            var point = new Point(int.Parse(args[3]), int.Parse(args[4]));
            var owner = new IntPtr(long.Parse(args[6]));
            var appVerbs = new[] { "copy", "cut", "delete", "rename" };
            invoked = false;
            var ok = ShellContextMenu.Show(source, paths, point, verb =>
            {
                var single = paths.Count == 1 && Directory.Exists(paths[0]);
                if (appVerbs.Contains(verb) || (single && verb is "paste" or "open"))
                {
                    SetForegroundWindow(owner); // the app runs it: give the keyboard back to the main window
                    output.WriteLine("VERB " + verb);
                    output.Flush();
                    return true;
                }
                invoked = true;
                return false;
            }, extended: args[5] == "1", showing: () =>
            {
                output.WriteLine("SHOWN");
                output.Flush();
            });
            if (!ok)
            {
                output.WriteLine("NOMENU");
                return 0;
            }
            if (!invoked) SetForegroundWindow(owner); // closed without a choice
        }
        else if (args[1] == "verb")
        {
            invoked = ShellContextMenu.Invoke(source.Handle, paths, args[3]);
        }
        else return 2;

        if (!invoked) return 0;
        output.WriteLine("INVOKED");
        output.Flush();
        WaitForOwnWindows(source.Handle);
        return 0;
    }

    /// <summary>
    /// Dialogs the shell opened (Properties, Open with, a 7-Zip window…) belong to this process: keep it alive,
    /// handling messages, until they are all closed.
    /// </summary>
    private static void WaitForOwnWindows(IntPtr hidden)
    {
        var frame = new DispatcherFrame();
        var started = Stopwatch.StartNew();
        var quietTicks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            quietTicks = HasVisibleWindows(hidden) ? 0 : quietTicks + 1;
            // At least 3 s for slow dialogs to appear; then quit after 2 s with no window. Never more than a day.
            if ((started.Elapsed > TimeSpan.FromSeconds(3) && quietTicks >= 4) || started.Elapsed > TimeSpan.FromHours(24))
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static bool HasVisibleWindows(IntPtr except)
    {
        var pid = (uint)Environment.ProcessId;
        var found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner == pid && h != except && IsWindowVisible(h)) found = true;
            return !found;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}

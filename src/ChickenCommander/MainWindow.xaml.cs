using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ChickenCommander.Services;
using ChickenCommander.ViewModels;
using ChickenCommander.Views;

namespace ChickenCommander;

public partial class MainWindow : Window, IDialogService
{
    private readonly MainViewModel vm;

    public MainWindow()
    {
        InitializeComponent();
        vm = new MainViewModel(this);
        DataContext = vm;

        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            panel.GotKeyboardFocus += (_, _) => vm.ActivePanel = panel.ViewModel!;
            panel.OpenRequested += _ => vm.OpenCurrent();
            panel.PathEntered += (p, text) =>
            {
                vm.ActivePanel = p.ViewModel!;
                vm.NavigateActive(text);
                p.FocusList();
            };
        }

        Loaded += (_, _) => ActivePanelView.FocusList();
    }

    private FilePanel ActivePanelView => vm.ActivePanel == vm.Left ? LeftPanel : RightPanel;

    // ---- Keyboard --------------------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        e.Handled = HandleGlobalKey(key, mods) || (!IsTextBoxFocused && HandleListKey(key, mods));
    }

    /// <summary>Keys that work everywhere in the main window, including the command line.</summary>
    private bool HandleGlobalKey(Key key, ModifierKeys mods)
    {
        switch (key, mods)
        {
            case (Key.Tab, ModifierKeys.None):
            case (Key.Tab, ModifierKeys.Shift):
                vm.SwitchPanelCommand.Execute(null);
                return true;
            case (Key.F1, ModifierKeys.None): ShowShortcuts(); return true;
            case (Key.F3, ModifierKeys.None): vm.ViewCommand.Execute(null); return true;
            case (Key.F4, ModifierKeys.None): vm.EditCommand.Execute(null); return true;
            case (Key.F5, ModifierKeys.None): vm.CopyCommand.Execute(null); return true;
            case (Key.F6, ModifierKeys.None): vm.MoveCommand.Execute(null); return true;
            case (Key.F6, ModifierKeys.Shift): vm.RenameCommand.Execute(null); return true;
            case (Key.F7, ModifierKeys.None): vm.MakeDirectoryCommand.Execute(null); return true;
            case (Key.F8, ModifierKeys.None): vm.DeleteCommand.Execute(null); return true;
            case (Key.F8, ModifierKeys.Shift): vm.DeletePermanentlyCommand.Execute(null); return true;
            case (Key.F1, ModifierKeys.Alt): LeftPanel.OpenDriveList(); return true;
            case (Key.F2, ModifierKeys.Alt): RightPanel.OpenDriveList(); return true;
            case (Key.R, ModifierKeys.Control): vm.RefreshCommand.Execute(null); return true;
            case (Key.U, ModifierKeys.Control): vm.SwapPanelsCommand.Execute(null); return true;
            case (Key.E, ModifierKeys.Control): vm.TargetEqualsSourceCommand.Execute(null); return true;
            case (Key.H, ModifierKeys.Control): vm.ToggleHiddenCommand.Execute(null); return true;
            case (Key.L, ModifierKeys.Control): ActivePanelView.FocusPathBox(); return true;
            default: return false;
        }
    }

    /// <summary>Keys for the file list. Typing printable characters goes to the command line (see PreviewTextInput).</summary>
    private bool HandleListKey(Key key, ModifierKeys mods)
    {
        var panel = vm.ActivePanel;
        var hasCommand = vm.CommandLine.Length > 0;

        switch (key, mods)
        {
            case (Key.Enter, ModifierKeys.None):
                if (hasCommand) vm.ExecuteCommandLineCommand.Execute(null);
                else vm.OpenCurrent();
                return true;
            case (Key.Enter, ModifierKeys.Control):
                // Ctrl+Enter copies the name under the cursor into the command line.
                if (panel.CurrentItem is { IsParentLink: false } item)
                    vm.CommandLine += (item.Entry.Name.Contains(' ') ? $"\"{item.Entry.Name}\"" : item.Entry.Name) + " ";
                return true;
            case (Key.Back, ModifierKeys.None):
                if (hasCommand) vm.CommandLine = vm.CommandLine[..^1];
                else panel.GoUp();
                return true;
            case (Key.PageUp, ModifierKeys.Control):
                panel.GoUp();
                return true;
            case (Key.Oem5, ModifierKeys.Control): // Ctrl+\ : go to the drive root
                panel.Navigate(Path.GetPathRoot(panel.CurrentPath)!);
                return true;
            case (Key.Escape, ModifierKeys.None):
                vm.CommandLine = string.Empty;
                return true;
            case (Key.Insert, ModifierKeys.None):
                panel.ToggleMark(panel.CurrentItem);
                ActivePanelView.MoveCursor(1);
                return true;
            case (Key.Space, ModifierKeys.None) when !hasCommand:
                panel.ToggleMark(panel.CurrentItem);
                return true;
            case (Key.Delete, ModifierKeys.None): vm.DeleteCommand.Execute(null); return true;
            case (Key.Delete, ModifierKeys.Shift): vm.DeletePermanentlyCommand.Execute(null); return true;
            case (Key.Add, ModifierKeys.None): vm.SelectGroupCommand.Execute(null); return true;
            case (Key.Subtract, ModifierKeys.None): vm.UnselectGroupCommand.Execute(null); return true;
            case (Key.Multiply, ModifierKeys.None): vm.InvertSelectionCommand.Execute(null); return true;
            case (Key.A, ModifierKeys.Control): vm.SelectAllCommand.Execute(null); return true;
            case (Key.D, ModifierKeys.Control): vm.UnselectAllCommand.Execute(null); return true;
            default: return false;
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (IsTextBoxFocused || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        vm.CommandLine += e.Text;
        e.Handled = true;
    }

    private bool IsTextBoxFocused => Keyboard.FocusedElement is TextBox;

    private void CommandLineBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            vm.ExecuteCommandLineCommand.Execute(null);
            ActivePanelView.FocusList();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CommandLine = string.Empty;
            ActivePanelView.FocusList();
            e.Handled = true;
        }
    }

    // ---- Menu handlers ---------------------------------------------------------------------

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenCmd_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = vm.ActivePanel.CurrentPath });

    private void OpenExplorer_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{vm.ActivePanel.CurrentPath}\"") { UseShellExecute = true });

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcuts();

    private void ShowShortcuts() => MessageBox.Show(this,
        """
        Tab                 Switch panel
        Enter               Open folder / run file (or run command line)
        Backspace           Parent folder
        Ctrl+\              Drive root
        Alt+F1 / Alt+F2     Choose drive (left / right)
        Ctrl+L              Edit path

        Insert / Space      Mark item
        Num + / Num -       Mark / unmark by mask
        Num *               Invert marks
        Ctrl+A / Ctrl+D     Mark all / unmark all

        F3 View    F4 Edit    F5 Copy    F6 Move
        Shift+F6 Rename       F7 New folder
        F8 / Del  Delete      Shift+Del  Delete permanently

        Ctrl+R  Refresh       Ctrl+H  Hidden files
        Ctrl+U  Swap panels   Ctrl+E  Target = Source
        Ctrl+Enter  Name to command line
        Typing goes to the command line.
        """, "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---- IDialogService --------------------------------------------------------------------

    public IntPtr OwnerHandle => new WindowInteropHelper(this).Handle;

    public string? Prompt(string title, string message, string initialValue, bool selectNameOnly = false)
    {
        var dialog = new InputDialog(title, message, initialValue, selectNameOnly) { Owner = this };
        var result = dialog.ShowDialog() == true ? dialog.Value : null;
        ActivePanelView.FocusList();
        return result;
    }

    public bool Confirm(string title, string message)
    {
        var ok = MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        ActivePanelView.FocusList();
        return ok;
    }

    public void ShowError(string title, string message)
    {
        // Errors can be raised while the window is still being constructed (e.g. start folder missing).
        if (IsLoaded) MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void ShowAbout() => new AboutWindow { Owner = this }.ShowDialog();
}

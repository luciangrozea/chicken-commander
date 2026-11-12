using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;
using ChickenCommander.Core.Settings;
using ChickenCommander.Services;
using ChickenCommander.Viewer;
using ChickenCommander.ViewModels;
using ChickenCommander.Views;

namespace ChickenCommander;

public partial class MainWindow : Window, IDialogService
{
    private readonly MainViewModel vm;
    private readonly SettingsStore settingsStore = SettingsStore.Default;
    private readonly OperationQueue operations = new();
    private OperationsWindow? operationsWindow;

    public MainWindow()
    {
        InitializeComponent();
        var settings = settingsStore.Load();
        ApplySavedBounds(settings.Window);

        vm = new MainViewModel(this, settings, operations);
        DataContext = vm;

        operations.Started += ShowOperationsWindow;
        operations.Completed += _ =>
        {
            // Hide the progress window once everything finished cleanly.
            if (!operations.HasActiveWork && operations.Items.All(o => o.State == OperationState.Done)) operationsWindow?.Hide();
        };
        vm.OperationsWindowRequested += ShowOperationsWindow;

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

        // Quick View follows the active panel's cursor, with a short delay so fast scrolling stays smooth.
        quickViewTimer.Tick += async (_, _) =>
        {
            quickViewTimer.Stop();
            await UpdateQuickViewContentAsync();
        };
        LeftQuickView.InfoChanged += () => LeftQuickInfo.Text = LeftQuickView.Info;
        RightQuickView.InfoChanged += () => RightQuickInfo.Text = RightQuickView.Info;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsQuickView) or nameof(MainViewModel.ActivePanel)) UpdateQuickViewLayout();
        };
        foreach (var p in new[] { vm.Left, vm.Right })
        {
            p.PropertyChanged += (s, e) =>
            {
                if (vm.IsQuickView && s == vm.ActivePanel && e.PropertyName == nameof(FilePanelViewModel.CurrentItem))
                {
                    quickViewTimer.Stop();
                    quickViewTimer.Start();
                }
            };
        }

        Loaded += (_, _) => ActivePanelView.FocusList();
    }

    private readonly System.Windows.Threading.DispatcherTimer quickViewTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private SearchWindow? searchWindow;

    // ---- Quick View ------------------------------------------------------------------------

    private void UpdateQuickViewLayout()
    {
        var on = vm.IsQuickView;
        var leftIsPreview = on && vm.ActivePanel == vm.Right;
        var rightIsPreview = on && vm.ActivePanel == vm.Left;
        LeftPanel.Visibility = leftIsPreview ? Visibility.Collapsed : Visibility.Visible;
        RightPanel.Visibility = rightIsPreview ? Visibility.Collapsed : Visibility.Visible;
        LeftQuickHost.Visibility = leftIsPreview ? Visibility.Visible : Visibility.Collapsed;
        RightQuickHost.Visibility = rightIsPreview ? Visibility.Visible : Visibility.Collapsed;
        if (!leftIsPreview) LeftQuickView.Close();
        if (!rightIsPreview) RightQuickView.Close();
        if (on) _ = UpdateQuickViewContentAsync();
        ActivePanelView.FocusList();
    }

    private async Task UpdateQuickViewContentAsync()
    {
        if (!vm.IsQuickView) return;
        var view = vm.ActivePanel == vm.Left ? RightQuickView : LeftQuickView;
        var item = vm.ActivePanel.CurrentItem;
        if (item is null)
        {
            view.ShowMessage(string.Empty);
        }
        else if (item.Entry.IsDirectory)
        {
            var path = item.Entry.FullPath;
            view.ShowMessage(await Task.Run(() => ViewerControl.DescribeFolder(path)));
        }
        else
        {
            await view.LoadAsync(item.Entry.FullPath);
        }
    }

    private FilePanel ActivePanelView => vm.ActivePanel == vm.Left ? LeftPanel : RightPanel;

    // ---- Window state ----------------------------------------------------------------------

    private void ApplySavedBounds(WindowSettings w)
    {
        Width = Math.Max(MinWidth, w.Width);
        Height = Math.Max(MinHeight, w.Height);
        if (!double.IsNaN(w.Left) && !double.IsNaN(w.Top))
        {
            // Only reuse the position if it is still on a visible screen (monitor may be gone).
            var visible = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (visible.Contains(new Point(w.Left + 50, w.Top + 20)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = w.Left;
                Top = w.Top;
            }
        }
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (operations.HasActiveWork &&
            !Confirm("Chicken Commander", "File operations are still running. Cancel them and exit?"))
        {
            e.Cancel = true;
            return;
        }
        foreach (var op in operations.Items.Where(o => !o.IsFinished)) op.CancelCommand.Execute(null);

        var settings = vm.CaptureSettings();
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        settings.Window = new WindowSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        try { settingsStore.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* settings are a convenience; never block exit */ }

        if (operationsWindow is not null)
        {
            operationsWindow.AllowClose = true;
            operationsWindow.Close();
        }
    }

    private void ShowOperationsWindow()
    {
        operationsWindow ??= new OperationsWindow { Owner = this, DataContext = operations };
        if (!operationsWindow.IsVisible) operationsWindow.Show();
    }

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
        const ModifierKeys CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
        switch (key, mods)
        {
            case (Key.Tab, ModifierKeys.None):
            case (Key.Tab, ModifierKeys.Shift):
                vm.SwitchPanelCommand.Execute(null);
                return true;
            case (Key.Tab, ModifierKeys.Control): vm.NextTabCommand.Execute(null); return true;
            case (Key.Tab, CtrlShift): vm.PreviousTabCommand.Execute(null); return true;
            case (Key.T, ModifierKeys.Control): vm.NewTabCommand.Execute(null); return true;
            case (Key.W, ModifierKeys.Control): vm.CloseTabCommand.Execute(null); return true;
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
            case (Key.Left, ModifierKeys.Alt): vm.GoBackCommand.Execute(null); return true;
            case (Key.Right, ModifierKeys.Alt): vm.GoForwardCommand.Execute(null); return true;
            case (Key.Down, ModifierKeys.Alt): vm.ShowHistoryCommand.Execute(null); return true;
            case (Key.D, ModifierKeys.Control): vm.ShowHotlistCommand.Execute(null); return true;
            case (Key.M, ModifierKeys.Control): vm.MultiRenameCommand.Execute(null); return true;
            case (Key.I, ModifierKeys.Control): vm.ChangeAttributesCommand.Execute(null); return true;
            case (Key.O, CtrlShift): vm.ShowOperationsCommand.Execute(null); return true;
            case (Key.F7, ModifierKeys.Alt): vm.SearchCommand.Execute(null); return true;
            case (Key.Q, ModifierKeys.Control): vm.ToggleQuickViewCommand.Execute(null); return true;
            case (Key.C, CtrlShift): vm.CompareFilesCommand.Execute(null); return true;
            case (Key.S, CtrlShift): vm.SyncDirectoriesCommand.Execute(null); return true;
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
            case (Key.Up, ModifierKeys.Control): vm.OpenInNewTabCommand.Execute(null); return true;
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
            case (Key.Add, ModifierKeys.Control):
            case (Key.A, ModifierKeys.Control):
                vm.SelectAllCommand.Execute(null);
                return true;
            case (Key.Subtract, ModifierKeys.Control):
            case (Key.A, ModifierKeys.Control | ModifierKeys.Shift):
                vm.UnselectAllCommand.Execute(null);
                return true;
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
        Alt+Left / Right    Back / forward      Alt+Down  Folder history
        Ctrl+D              Directory hotlist
        Ctrl+\              Drive root
        Alt+F1 / Alt+F2     Choose drive (left / right)
        Ctrl+L              Edit path

        Ctrl+T / Ctrl+W     New / close tab
        Ctrl+Tab            Next tab (Shift: previous)
        Ctrl+Up             Open folder under cursor in new tab

        Insert / Space      Mark item
        Num + / Num -       Mark / unmark by mask
        Num *               Invert marks
        Ctrl+A / Ctrl+Shift+A  Mark all / unmark all

        F3 View    F4 Edit    F5 Copy    F6 Move
        Shift+F6 Rename       F7 New folder
        F8 / Del  Delete      Shift+Del  Delete permanently
        Ctrl+M  Multi-Rename  Ctrl+I  Attributes

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

    public (string Target, OverwriteMode Mode)? AskTransfer(TransferKind kind, string description, string initialTarget)
    {
        var dialog = new TransferDialog(kind, description, initialTarget) { Owner = this };
        var ok = dialog.ShowDialog() == true;
        ActivePanelView.FocusList();
        return ok ? (dialog.Target, dialog.Mode) : null;
    }

    public ConflictChoice ResolveConflict(ConflictInfo c)
    {
        var details =
            $"New:      {SizeFormatter.Exact(c.SourceSize),15} bytes   {c.SourceModified:dd.MM.yyyy HH:mm:ss}\n" +
            $"Existing: {SizeFormatter.Exact(c.DestinationSize),15} bytes   {c.DestinationModified:dd.MM.yyyy HH:mm:ss}";
        ConflictChoice[] choices =
        [
            ConflictChoice.Overwrite, ConflictChoice.OverwriteAll, ConflictChoice.OverwriteOlderAll,
            ConflictChoice.Skip, ConflictChoice.SkipAll, ConflictChoice.Rename, ConflictChoice.RenameAll, ConflictChoice.Cancel,
        ];
        string[] labels = ["Overwrite", "Overwrite all", "Overwrite all older", "Skip", "Skip all", "Rename", "Rename all", "Cancel"];
        var dialog = new ChoiceDialog("File exists", $"Overwrite \"{c.Destination}\"?", details, labels, defaultIndex: 0, cancelIndex: 7)
        {
            Owner = OwnerForPopups,
        };
        dialog.ShowDialog();
        return choices[dialog.Choice];
    }

    public ErrorChoice AskOnError(string path, string message)
    {
        var dialog = new ChoiceDialog("Error", $"Problem with \"{path}\"", message, ["Retry", "Skip", "Cancel all"], defaultIndex: 0, cancelIndex: 2)
        {
            Owner = OwnerForPopups,
        };
        dialog.ShowDialog();
        return dialog.Choice switch { 0 => ErrorChoice.Retry, 1 => ErrorChoice.Skip, _ => ErrorChoice.Cancel };
    }

    public bool ShowMultiRename(IReadOnlyList<RenameSource> items)
    {
        var model = new MultiRenameViewModel(items);
        new MultiRenameWindow(model) { Owner = this }.ShowDialog();
        return model.AnyRenamed;
    }

    public bool ShowAttributes(IReadOnlyList<FileEntry> items)
    {
        var dialog = new AttributesWindow(items) { Owner = this };
        if (dialog.ShowDialog() != true) return false;
        if (dialog.Errors.Count > 0)
        {
            var list = string.Join("\n", dialog.Errors.Take(10).Select(e => $"{e.Path}: {e.Error}"));
            ShowError("Change attributes", $"Some items could not be changed:\n\n{list}");
        }
        return true;
    }

    public void ShowMenu(IReadOnlyList<MenuEntry> entries)
    {
        var anchor = ActivePanelView.MenuAnchor;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        foreach (var entry in entries)
        {
            if (entry.Action is null)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = entry.Header, InputGestureText = entry.Hint ?? string.Empty };
            var action = entry.Action;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => ActivePanelView.FocusList();
        menu.IsOpen = true;
        if (menu.Items.Count > 0 && menu.Items[0] is MenuItem first) first.Focus();
    }

    public void ShowLister(IReadOnlyList<string> files, int index)
    {
        if (files.Count == 0) return;
        new ListerWindow(files, index) { Owner = this }.Show();
    }

    public void ShowSearch(string startFolder)
    {
        if (searchWindow is { IsLoaded: true })
        {
            searchWindow.Activate();
            return;
        }
        searchWindow = new SearchWindow(startFolder,
            goToFile: path =>
            {
                Activate();
                vm.GoToFile(path);
            },
            viewFiles: ShowLister) { Owner = this };
        searchWindow.Closed += (_, _) => searchWindow = null;
        searchWindow.Show();
    }

    public void ShowCompare(string left, string right)
    {
        string? binary;
        try
        {
            binary = CompareWindow.BinarySummary(left, right);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Compare by content", ex.Message);
            return;
        }
        if (binary is not null)
        {
            MessageBox.Show(this, binary, "Compare by content", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new CompareWindow(left, right) { Owner = this }.Show();
    }

    public bool ShowSync(string left, string right)
    {
        var window = new SyncWindow(left, right) { Owner = this };
        window.ShowDialog();
        return window.Changed;
    }

    /// <summary>Conflict prompts belong to the progress window when it is open, otherwise to the main window.</summary>
    private Window OwnerForPopups => operationsWindow is { IsVisible: true } w ? w : this;
}

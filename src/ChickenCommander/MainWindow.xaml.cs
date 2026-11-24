using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using ChickenCommander.Commands;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;
using ChickenCommander.Core.Remote;
using ChickenCommander.Core.Settings;
using ChickenCommander.Core.Tools;
using ChickenCommander.Core.Vfs;
using System.Windows.Media;
using ChickenCommander.Services;
using ChickenCommander.Themes;
using ChickenCommander.Viewer;
using ChickenCommander.ViewModels;
using ChickenCommander.Views;

namespace ChickenCommander;

public partial class MainWindow : Window, IDialogService, IRemoteTrust
{
    private readonly MainViewModel vm;
    private readonly SettingsStore settingsStore = SettingsStore.Default;
    private readonly OperationQueue operations = new();
    private OperationsWindow? operationsWindow;

    public MainWindow()
    {
        var settings = settingsStore.Load();
        ThemeManager.Apply(settings.Theme); // before the window is built, so it never flashes in the wrong colours
        InitializeComponent();
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
            panel.ContextMenuRequested += OnContextMenuRequested;
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

        ApplyPreferences();
        Loaded += (_, _) => ActivePanelView.FocusList();
    }

    // ---- Preferences: theme, font, shortcuts, button bar --------------------------------------

    /// <summary>Applies everything from the settings that affects the window; called at start and after the Settings dialog.</summary>
    private void ApplyPreferences()
    {
        var s = vm.Settings;
        ThemeManager.Apply(s.Theme);
        Application.Current.Resources["ListFontSize"] = Math.Clamp(s.ListFontSize, 9, 24);
        shortcuts = new ShortcutMap(CommandRegistry.DefaultGestures, s.Shortcuts);
        UpdateMenuGestures(MainMenu.Items);
        BuildButtonBar();
        ButtonBarHost.Visibility = s.ShowButtonBar ? Visibility.Visible : Visibility.Collapsed;
        ButtonBarMenuItem.IsChecked = s.ShowButtonBar;
    }

    /// <summary>Shows each menu item's current shortcut (they can be changed in Settings).</summary>
    private void UpdateMenuGestures(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            if (item.Tag is string id) item.InputGestureText = string.Join(" / ", shortcuts.GesturesFor(id).Select(KeyNames.Display));
            UpdateMenuGestures(item.Items);
        }
    }

    private void BuildButtonBar()
    {
        ButtonBarPanel.Children.Clear();
        foreach (var item in vm.Settings.ButtonBar ?? CommandRegistry.DefaultButtonBar())
        {
            if (item.Kind == ButtonKind.Separator)
            {
                ButtonBarPanel.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 4, 6, 4), Background = (Brush)FindResource("AppBorderBrush") });
                continue;
            }

            var command = item.Kind == ButtonKind.Command ? CommandRegistry.Find(item.CommandId) : null;
            if (item.Kind == ButtonKind.Command && command is null) continue; // unknown id in an old settings file
            var glyph = item.Glyph ?? command?.Glyph ?? CommandRegistry.ProgramGlyph;
            var tip = item.Label ?? command?.Title ?? Path.GetFileNameWithoutExtension(item.Program) ?? "Program";
            if (command is not null && shortcuts.GesturesFor(command.Id) is { Count: > 0 } gestures)
                tip += $"  ({KeyNames.Display(gestures[0])})";

            var button = new Button
            {
                Content = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16 },
                ToolTip = tip,
                Focusable = false,
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Tag = item,
            };
            button.SetResourceReference(ForegroundProperty, "TextBrush");
            button.Click += ButtonBar_Click;
            ButtonBarPanel.Children.Add(button);
        }
    }

    private void ButtonBar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ButtonBarItem item }) return;
        if (item.Kind == ButtonKind.Command)
        {
            CommandRegistry.Find(item.CommandId)?.Execute(this);
            return;
        }

        // External program with placeholders (%P folder, %N name, %F full path, %S selection, %T other panel).
        var panel = vm.ActivePanel;
        if (!panel.IsLocal && (item.Arguments ?? string.Empty).Contains('%'))
        {
            // %N / %F / %S would name files that only exist inside the archive or on the server.
            ShowError(item.Label ?? "Program", "Programs with file parameters work on local folders. Copy the files out of the archive or server first.");
            return;
        }
        var current = panel.CurrentItem is { IsParentLink: false } c ? c.Entry.Name : null;
        var selection = panel.SelectionOrCurrent().Select(i => i.Entry.FullPath).ToList();
        var args = ButtonArguments.Expand(item.Arguments ?? string.Empty, vm.WorkingFolder, current, selection, vm.InactivePanel.CurrentPath);
        StartInWorkingDirectory(item.Program ?? string.Empty, args);
        ActivePanelView.FocusList();
    }

    private void ToggleButtonBar_Click(object sender, RoutedEventArgs e)
    {
        vm.Settings.ShowButtonBar = !vm.Settings.ShowButtonBar;
        ApplyPreferences();
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/luciangrozea/chicken-commander") { UseShellExecute = true });

    /// <summary>Opens the Settings dialog and applies the changes right away.</summary>
    internal void ShowSettings()
    {
        var window = new SettingsWindow(vm.Settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            vm.ApplySettingsChanges();
            ApplyPreferences();
            try { settingsStore.Save(vm.CaptureSettings()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("Settings", ex.Message); }
        }
        ActivePanelView.FocusList();
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

    internal FilePanel ActivePanelView => vm.ActivePanel == vm.Left ? LeftPanel : RightPanel;

    private ShortcutMap shortcuts = new(CommandRegistry.DefaultGestures);

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
        vm.Cleanup();
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
        // A drop-down or menu that has the keyboard (drive list, hotlist, history) handles its own keys:
        // Enter picks the entry there instead of opening the item under the panel cursor.
        if (IsPopupControlFocused) return;
        if (key == Key.Escape && vm.ActivePanel.IsBusy)
        {
            vm.ActivePanel.CancelBusy(); // stop reading a big archive
            e.Handled = true;
            return;
        }
        e.Handled =(!IsTextBoxFocused && HandleContextKey(key, mods)) || RunShortcut(key, mods);
    }

    private static bool IsPopupControlFocused =>
        Keyboard.FocusedElement is ComboBox or ComboBoxItem or MenuItem or System.Windows.Controls.ContextMenu;

    /// <summary>Looks the key up in the (customizable) shortcut map and runs the command.</summary>
    private bool RunShortcut(Key key, ModifierKeys mods)
    {
        if (IsTextBoxFocused && !KeyNames.AllowedInTextBox(key, mods)) return false;
        if (KeyNames.From(key, mods) is not { } gesture) return false;
        if (CommandRegistry.Find(shortcuts.CommandFor(gesture)) is not { } command) return false;
        command.Execute(this);
        return true;
    }

    /// <summary>
    /// Built-in keys whose meaning depends on the command line, so they are not remappable:
    /// Enter, Ctrl+Enter, Backspace, Esc and Space. Typing printable characters goes to the command line.
    /// </summary>
    private bool HandleContextKey(Key key, ModifierKeys mods)
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
            case (Key.Escape, ModifierKeys.None) when hasCommand:
                vm.CommandLine = string.Empty;
                return true;
            case (Key.Space, ModifierKeys.None) when !hasCommand:
                panel.ToggleMark(panel.CurrentItem);
                return true;
            default:
                return false;
        }
    }

    // ---- Right-click menu --------------------------------------------------------------------

    /// <summary>Menu key / Shift+F10: the menu of the item under the cursor.</summary>
    internal void ShowContextMenuAtCursor() => ActivePanelView.RequestContextMenuAtCursor();

    /// <summary>
    /// Right-click on files: our own menu, which opens instantly and loads no third-party code (Open, Cut, Copy,
    /// Rename, Delete, Properties…), with "Show more options" for the full Explorer menu, as on Windows 11.
    /// Shift+right-click opens the Explorer menu directly. Empty space or "..": a folder menu (Paste, New folder…).
    /// </summary>
    private void OnContextMenuRequested(FilePanel view, Point point, FileItemViewModel? item, bool explorerMenu)
    {
        if (view.ViewModel is not { } panel) return;
        vm.ActivePanel = panel;
        if (item is null || item.IsParentLink)
        {
            ShowPopupMenu(view, point, FolderMenuEntries(panel));
            return;
        }
        var selection = panel.SelectionOrCurrent();
        if (selection.Count == 0) return;
        if (!panel.IsLocal)
        {
            ShowPopupMenu(view, point, VirtualItemMenuEntries(panel));
            return;
        }
        if (explorerMenu)
        {
            ShowExplorerMenu(view, point, panel, selection, extended: true);
            return;
        }
        ShowPopupMenu(view, point, ItemMenuEntries(view, point, panel, selection));
    }

    private bool explorerMenuStarting;

    /// <summary>
    /// The full Explorer menu, shown by a helper process (see <see cref="ShellHelperProcess"/>): shell extensions run
    /// there, so one that crashes or hangs cannot take Chicken Commander down with it.
    /// </summary>
    private async void ShowExplorerMenu(FilePanel view, Point point, FilePanelViewModel panel, IReadOnlyList<FileItemViewModel> selection, bool extended)
    {
        if (explorerMenuStarting) return;
        explorerMenuStarting = true;
        var paths = selection.Select(i => i.Entry.FullPath).ToList();
        var screen = view.PointToScreen(point);
        Mouse.OverrideCursor = Cursors.AppStarting;
        void Shown()
        {
            Mouse.OverrideCursor = null;
            explorerMenuStarting = false;
        }
        try
        {
            var (outcome, detail) = await ShellHelperProcess.ShowMenuAsync(new WindowInteropHelper(this).Handle, paths, screen, extended,
                onVerb: verb =>
                {
                    Activate();
                    HandleShellVerb(verb, panel, selection);
                },
                onShellCommand: () => RefreshAfterShellCommand(panel),
                onShown: Shown);
            switch (outcome)
            {
                case ShellHelperProcess.Outcome.Crashed:
                    App.LogError("Explorer menu helper crashed", detail);
                    ShowError("Explorer menu",
                        "The Explorer menu could not be shown: a program that adds itself to that menu (a shell extension) " +
                        $"crashed ({detail}).\n\nChicken Commander is not affected. Its own right-click menu still works.");
                    break;
                case ShellHelperProcess.Outcome.TimedOut:
                    App.LogError("Explorer menu helper timed out", detail);
                    ShowError("Explorer menu", detail + "\n\nA program that adds itself to that menu (a shell extension) seems stuck. Its own right-click menu still works.");
                    break;
                case ShellHelperProcess.Outcome.NoMenu:
                    ShowPopupMenu(view, point, ItemMenuEntries(view, point, panel, selection, withMoreOptions: false));
                    break;
                case ShellHelperProcess.Outcome.NotStarted:
                    ShowError("Explorer menu", detail);
                    break;
            }
        }
        finally
        {
            Shown();
        }
    }

    /// <summary>A shell command (7-Zip, "Send to", Properties…) may have changed files: reload, keeping marks and cursor.</summary>
    private void RefreshAfterShellCommand(FilePanelViewModel panel)
    {
        Dispatcher.BeginInvoke(() =>
        {
            panel.Refresh();
            if (!ReferenceEquals(panel, vm.InactivePanel) && string.Equals(vm.InactivePanel.CurrentPath, panel.CurrentPath, StringComparison.OrdinalIgnoreCase))
                vm.InactivePanel.Refresh();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Properties / Open with run in the helper process too: they load the same shell extensions.</summary>
    private async void RunShellVerb(IReadOnlyList<string> paths, string verb, FilePanelViewModel panel)
    {
        var (outcome, detail) = await ShellHelperProcess.InvokeAsync(paths, verb, () => RefreshAfterShellCommand(panel));
        if (outcome == ShellHelperProcess.Outcome.Crashed)
        {
            App.LogError($"Shell verb '{verb}' helper crashed", detail);
            ShowError(verb == "properties" ? "Properties" : "Open with",
                $"Windows could not show this dialog: a shell extension installed on this PC crashed ({detail}).\n\nChicken Commander is not affected.");
        }
        else if (outcome == ShellHelperProcess.Outcome.NotStarted)
        {
            ShowError(verb == "properties" ? "Properties" : "Open with", detail);
        }
    }

    /// <summary>Our own menu for files and folders on local and network drives.</summary>
    private List<MenuEntry> ItemMenuEntries(FilePanel view, Point point, FilePanelViewModel panel, IReadOnlyList<FileItemViewModel> selection,
        bool withMoreOptions = true)
    {
        var paths = selection.Select(i => i.Entry.FullPath).ToList();
        var singleFolder = selection is [{ Entry.IsDirectory: true } folder] ? folder : null;
        var entries = new List<MenuEntry>
        {
            new("_Open", () => vm.OpenCurrent(), "Enter"),
        };
        if (selection is [{ Entry.IsDirectory: false } file])
            entries.Add(new MenuEntry("Open _with...", () => RunShellVerb([file.Entry.FullPath], "openas", panel)));
        entries.Add(new MenuEntry("_View", () => vm.ViewCommand.Execute(null), Gesture("View")));
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry("Cu_t", () => vm.PutOnClipboard(cut: true), Gesture("CutToClipboard")));
        entries.Add(new MenuEntry("_Copy", () => vm.PutOnClipboard(cut: false), Gesture("CopyToClipboard")));
        if (FileClipboard.ContainsFiles())
            entries.Add(new MenuEntry("_Paste", () => vm.PasteInto(singleFolder?.Entry.FullPath ?? panel.CurrentPath), Gesture("Paste")));
        entries.Add(new MenuEntry("Copy as pat_h", () => CopyAsPath(paths)));
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry("Rena_me...", () =>
        {
            if (selection.Count > 1) vm.MultiRenameCommand.Execute(null);
            else vm.RenameCommand.Execute(null);
        }, Gesture(selection.Count > 1 ? "MultiRename" : "Rename")));
        entries.Add(new MenuEntry("_Delete", () => vm.DeleteCommand.Execute(null), Gesture("Delete")));
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry("P_ack...", () => vm.PackCommand.Execute(null), Gesture("Pack")));
        entries.Add(new MenuEntry("P_roperties", () => RunShellVerb(paths, "properties", panel)));
        if (withMoreOptions)
        {
            entries.Add(MenuEntry.Separator);
            entries.Add(new MenuEntry("_Show more options", () => ShowExplorerMenu(view, point, panel, selection, extended: false), "Shift+right-click"));
        }
        return entries;
    }

    private void CopyAsPath(IReadOnlyList<string> paths)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => $"\"{p}\"")));
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            ShowError("Copy as path", ex.Message); // another program holds the clipboard
        }
    }

    /// <summary>Explorer verbs the app runs itself, so marks, refresh and confirmations behave like the F-keys.</summary>
    private bool HandleShellVerb(string verb, FilePanelViewModel panel, IReadOnlyList<FileItemViewModel> selection)
    {
        switch (verb)
        {
            case "copy":
                vm.PutOnClipboard(cut: false);
                return true;
            case "cut":
                vm.PutOnClipboard(cut: true);
                return true;
            case "paste" when selection is [{ Entry.IsDirectory: true } folder]:
                vm.PasteInto(folder.Entry.FullPath);
                return true;
            case "delete":
                vm.DeleteCommand.Execute(null);
                return true;
            case "rename":
                if (selection.Count > 1) vm.MultiRenameCommand.Execute(null);
                else vm.RenameCommand.Execute(null);
                return true;
            case "open" when selection is [{ Entry.IsDirectory: true } dir]:
                // Folders open in the panel, not in a new Explorer window.
                panel.TryEnter(dir);
                return true;
            default:
                return false;
        }
    }

    private List<MenuEntry> FolderMenuEntries(FilePanelViewModel panel)
    {
        var entries = new List<MenuEntry>();
        if (panel.Location != LocationKind.Archive && FileClipboard.ContainsFiles())
            entries.Add(new MenuEntry("_Paste", () => vm.PasteInto(panel.CurrentPath), Gesture("Paste")));
        if (panel.Location != LocationKind.Archive)
            entries.Add(new MenuEntry("_New folder...", () => vm.MakeDirectoryCommand.Execute(null), Gesture("MakeDirectory")));
        if (entries.Count > 0) entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry("Select _all", () => panel.MarkAll(true), Gesture("SelectAll")));
        entries.Add(new MenuEntry("_Refresh", () => vm.RefreshCommand.Execute(null), Gesture("Refresh")));
        if (panel.IsLocal)
        {
            entries.Add(MenuEntry.Separator);
            entries.Add(new MenuEntry("Open in _Explorer", () => StartInWorkingDirectory("explorer.exe", ".")));
            entries.Add(new MenuEntry("_Command prompt here", () => StartInWorkingDirectory("cmd.exe", null)));
            entries.Add(MenuEntry.Separator);
            entries.Add(new MenuEntry("Pr_operties", () => RunShellVerb([panel.CurrentPath], "properties", panel)));
        }
        return entries;
    }

    private List<MenuEntry> VirtualItemMenuEntries(FilePanelViewModel panel)
    {
        var entries = new List<MenuEntry>
        {
            new("_View", () => vm.ViewCommand.Execute(null), Gesture("View")),
            new("_Copy to other panel...", () => vm.CopyCommand.Execute(null), Gesture("Copy")),
        };
        if (panel.Location == LocationKind.Remote)
        {
            entries.Add(new MenuEntry("Re_name...", () => vm.RenameCommand.Execute(null), Gesture("Rename")));
            entries.Add(new MenuEntry("_Delete", () => vm.DeleteCommand.Execute(null), Gesture("Delete")));
        }
        return entries;
    }

    private string? Gesture(string commandId) =>
        shortcuts.GesturesFor(commandId) is { Count: > 0 } g ? KeyNames.Display(g[0]) : null;

    private void ShowPopupMenu(FilePanel view, Point point, IReadOnlyList<MenuEntry> entries)
    {
        // Opens with its top-left corner at the click point, like Explorer's menus. (Placement "Bottom" would follow the
        // tablet "handedness" setting and open to the left of the pointer on many laptops.) WPF still flips it at screen edges.
        var menu = new ContextMenu
        {
            PlacementTarget = view,
            Placement = PlacementMode.Custom,
            // Custom placement works in device pixels relative to the target; WPF still moves it to stay on screen.
            // Like Windows menus: below-right of the pointer, or flipped left / up when that does not fit on the screen.
            CustomPopupPlacementCallback = (size, _, _) =>
            {
                var dpi = VisualTreeHelper.GetDpi(view);
                var p = new Point(point.X * dpi.DpiScaleX, point.Y * dpi.DpiScaleY);
                return
                [
                    new CustomPopupPlacement(p, PopupPrimaryAxis.None),
                    new CustomPopupPlacement(new Point(p.X - size.Width, p.Y), PopupPrimaryAxis.None),
                    new CustomPopupPlacement(new Point(p.X, p.Y - size.Height), PopupPrimaryAxis.None),
                    new CustomPopupPlacement(new Point(p.X - size.Width, p.Y - size.Height), PopupPrimaryAxis.None),
                ];
            },
        };
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
        menu.Closed += (_, _) => view.FocusList();
        menu.IsOpen = true;
    }

    // ---- Helpers used by commands ------------------------------------------------------------

    internal MainViewModel Vm => vm;

    internal void GoToRoot()
    {
        var parent = vm.ActivePanel.CurrentPath;
        while (vm.Vfs.GetParent(parent) is { } p) parent = p;
        vm.ActivePanel.Navigate(parent);
    }

    internal void MarkAndMoveDown()
    {
        vm.ActivePanel.ToggleMark(vm.ActivePanel.CurrentItem);
        ActivePanelView.MoveCursor(1);
    }

    internal void StartInWorkingDirectory(string program, string? arguments)
    {
        try
        {
            CommandRegistry.Start(program, arguments, vm.WorkingFolder);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError(program, ex.Message);
        }
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (IsTextBoxFocused || IsPopupControlFocused || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
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

    /// <summary>Menu items carry the command id in their Tag, so they run exactly what the shortcut runs.</summary>
    private void Command_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && CommandRegistry.Find(id) is { } command) command.Execute(this);
    }

    /// <summary>F1: the current shortcuts (including the user's changes), grouped by category.</summary>
    internal void ShowShortcuts()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Enter  open · Backspace  parent folder · Space  mark · Esc  clear command line");
        sb.AppendLine("Typing goes to the command line; Ctrl+Enter adds the name under the cursor.");
        foreach (var group in CommandRegistry.All.GroupBy(c => c.Category))
        {
            sb.AppendLine();
            sb.AppendLine(group.Key.ToUpperInvariant());
            foreach (var c in group)
            {
                var gestures = shortcuts.GesturesFor(c.Id);
                if (gestures.Count > 0) sb.AppendLine($"  {string.Join(" / ", gestures.Select(KeyNames.Display)),-26} {c.Title}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Change shortcuts in Settings (Ctrl+,) → Keyboard.");
        ShowTextWindow("Keyboard shortcuts", sb.ToString());
    }

    /// <summary>
    /// A long text in a scrollable window that always fits on the screen
    /// (a message box grows taller than the screen and hides its OK button).
    /// </summary>
    private void ShowTextWindow(string title, string text)
    {
        var body = new TextBlock { Text = text, FontFamily = new FontFamily("Consolas"), FontSize = 13, Margin = new Thickness(16, 12, 16, 12) };
        body.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        var layout = new DockPanel();
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var window = new Window
        {
            Title = title,
            Owner = this,
            Content = layout,
            Width = 640,
            Height = Math.Min(760, SystemParameters.WorkArea.Height * 0.85),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        window.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        close.Click += (_, _) => window.Close();
        window.ShowDialog();
        ActivePanelView.FocusList();
    }

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
            // Headers here are folder names and paths: "_" is a real character, not an access key.
            var item = new MenuItem { Header = entry.Header.Replace("_", "__"), InputGestureText = entry.Hint ?? string.Empty };
            var action = entry.Action;
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => ActivePanelView.FocusList();
        menu.IsOpen = true;
        if (menu.Items.Count > 0 && menu.Items[0] is MenuItem first) first.Focus();
    }

    public (string Path, PackFormat Format)? AskPack(string description, string defaultPath)
    {
        var dialog = new PackDialog(description, defaultPath) { Owner = this };
        var ok = dialog.ShowDialog() == true;
        ActivePanelView.FocusList();
        return ok ? (dialog.ArchivePath, dialog.Format) : null;
    }

    public (RemoteConnection Connection, string? Password)? ShowConnections(List<RemoteConnection> connections)
    {
        var dialog = new ConnectionsWindow(connections) { Owner = this };
        var ok = dialog.ShowDialog() == true && dialog.Chosen is not null;
        ActivePanelView.FocusList();
        return ok ? (dialog.Chosen!, dialog.TypedPassword) : null;
    }

    public string? AskPassword(string title, string message)
    {
        var dialog = new PasswordDialog(title, message) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    public (ChecksumKind Kind, bool OnePerFile)? AskChecksum(int fileCount)
    {
        var dialog = new ChecksumDialog(fileCount) { Owner = this };
        var ok = dialog.ShowDialog() == true;
        ActivePanelView.FocusList();
        return ok ? (dialog.Kind, dialog.OnePerFile) : null;
    }

    public void ShowVerifyResults(string checksumFile, IReadOnlyList<VerifyResult> results) =>
        new VerifyResultsWindow(checksumFile, results) { Owner = this }.ShowDialog();

    public IRemoteTrust Trust => this;

    // SSH.NET / FluentFTP raise these on their own threads; the question is asked on the UI thread.
    bool IRemoteTrust.ConfirmHostKey(RemoteConnection connection, string fingerprint, bool changed) =>
        OnUiThread(() =>
        {
            var message = changed
                ? $"WARNING: the host key of {connection.Host} has CHANGED.\n\nThis can mean someone is intercepting the connection, or the server was reinstalled."
                : $"First connection to {connection.Host}. Check that this is the server's key before trusting it.";
            var dialog = new ChoiceDialog("Server key", message, fingerprint, ["Trust and connect", "Cancel"], defaultIndex: changed ? 1 : 0, cancelIndex: 1) { Owner = this };
            dialog.ShowDialog();
            return dialog.Choice == 0;
        });

    bool IRemoteTrust.ConfirmCertificate(RemoteConnection connection, string subject, string hash, string problem) =>
        OnUiThread(() =>
        {
            var dialog = new ChoiceDialog("Certificate", $"The TLS certificate of {connection.Host} is not trusted by Windows ({problem}).",
                $"{subject}\nSHA-1: {hash}", ["Accept for this server", "Cancel"], defaultIndex: 1, cancelIndex: 1) { Owner = this };
            dialog.ShowDialog();
            return dialog.Choice == 0;
        });

    private T OnUiThread<T>(Func<T> func) => Dispatcher.CheckAccess() ? func() : Dispatcher.Invoke(func);

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

    public async void ShowCompare(string left, string right)
    {
        string? binary;
        // Comparing two big files byte by byte takes a while: do it off the UI thread.
        Mouse.OverrideCursor = Cursors.AppStarting;
        try
        {
            binary = await Task.Run(() => CompareWindow.BinarySummary(left, right));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Compare by content", ex.Message);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
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

using System.Diagnostics;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;
using ChickenCommander.Core.Settings;
using ChickenCommander.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChickenCommander.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDialogService dialogs;
    private readonly AppSettings settings;

    [ObservableProperty] private FilePanelViewModel activePanel = null!;
    [ObservableProperty] private string commandLine = string.Empty;

    /// <summary>When on, the inactive panel shows a preview of the item under the active panel's cursor.</summary>
    [ObservableProperty] private bool isQuickView;

    public MainViewModel(IDialogService dialogs, AppSettings settings, OperationQueue operations)
    {
        this.dialogs = dialogs;
        this.settings = settings;
        Operations = operations;
        Operations.Completed += OnOperationCompleted;

        Left = new FilePanelViewModel { ShowHidden = settings.ShowHidden };
        Right = new FilePanelViewModel { ShowHidden = settings.ShowHidden };
        Left.ErrorOccurred += msg => dialogs.ShowError("Chicken Commander", msg);
        Right.ErrorOccurred += msg => dialogs.ShowError("Chicken Commander", msg);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        Left.Restore(settings.Left, home);
        Right.Restore(settings.Right, systemRoot);

        ActivePanel = settings.LeftIsActive ? Left : Right;
    }

    public FilePanelViewModel Left { get; }
    public FilePanelViewModel Right { get; }
    public FilePanelViewModel InactivePanel => ActivePanel == Left ? Right : Left;
    public OperationQueue Operations { get; }

    /// <summary>Prompt shown left of the command line, e.g. "C:\Users&gt;".</summary>
    public string CommandPrompt => ActivePanel.CurrentPath + ">";

    public bool ShowHidden => Left.ShowHidden;

    /// <summary>Copies the current state into the settings object (the window saves it on exit).</summary>
    public AppSettings CaptureSettings()
    {
        settings.Left = Left.ToSettings();
        settings.Right = Right.ToSettings();
        settings.LeftIsActive = ActivePanel == Left;
        settings.ShowHidden = ShowHidden;
        return settings;
    }

    partial void OnActivePanelChanged(FilePanelViewModel? oldValue, FilePanelViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsActive = false;
            oldValue.PropertyChanged -= OnActivePanelPropertyChanged;
        }
        newValue.IsActive = true;
        newValue.PropertyChanged += OnActivePanelPropertyChanged;
        OnPropertyChanged(nameof(InactivePanel));
        OnPropertyChanged(nameof(CommandPrompt));
    }

    private void OnActivePanelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePanelViewModel.CurrentPath)) OnPropertyChanged(nameof(CommandPrompt));
    }

    [RelayCommand]
    private void SwitchPanel()
    {
        ActivePanel = InactivePanel;
        ActivePanel.RequestFocus();
    }

    // ---- F-key bar -------------------------------------------------------------------------

    /// <summary>F3: built-in viewer over the marked files (or all files of the folder), starting at the cursor.</summary>
    [RelayCommand]
    private void View()
    {
        var current = ActivePanel.CurrentItem;
        if (current is null || current.Entry.IsDirectory) return;
        var marked = ActivePanel.MarkedItems.Where(i => !i.Entry.IsDirectory).ToList();
        var files = (marked.Count > 1 ? marked : ActivePanel.Items.Where(i => !i.Entry.IsDirectory))
            .Select(i => i.Entry.FullPath).ToList();
        var index = files.FindIndex(f => string.Equals(f, current.Entry.FullPath, StringComparison.OrdinalIgnoreCase));
        dialogs.ShowLister(files, Math.Max(0, index));
    }

    [RelayCommand]
    private void Edit()
    {
        var item = ActivePanel.CurrentItem;
        if (item is null || item.Entry.IsDirectory) return;
        ShellOpen("notepad.exe", Quote(item.Entry.FullPath));
    }

    [RelayCommand]
    private void ToggleQuickView() => IsQuickView = !IsQuickView;

    [RelayCommand]
    private void Search() => dialogs.ShowSearch(ActivePanel.CurrentPath);

    /// <summary>Compares two marked files in the active panel, or the files under both cursors.</summary>
    [RelayCommand]
    private void CompareFiles()
    {
        var marked = ActivePanel.MarkedItems.Where(i => !i.Entry.IsDirectory).ToList();
        string? left, right;
        if (marked.Count == 2)
        {
            (left, right) = (marked[0].Entry.FullPath, marked[1].Entry.FullPath);
        }
        else
        {
            left = ActivePanel.CurrentItem is { Entry.IsDirectory: false } a ? a.Entry.FullPath : null;
            right = InactivePanel.CurrentItem is { Entry.IsDirectory: false } b ? b.Entry.FullPath : null;
            if (ActivePanel == Right) (left, right) = (right, left); // keep the left panel's file on the left
        }
        if (left is null || right is null)
        {
            dialogs.ShowError("Compare by content", "Mark two files in one panel, or put the cursor on a file in each panel.");
            return;
        }
        dialogs.ShowCompare(left, right);
    }

    [RelayCommand]
    private void SyncDirectories()
    {
        if (dialogs.ShowSync(Left.CurrentPath, Right.CurrentPath))
        {
            Left.Refresh();
            Right.Refresh();
        }
        ActivePanel.RequestFocus();
    }

    /// <summary>Puts the active panel on a file (used by search results).</summary>
    public void GoToFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return;
        ActivePanel.Navigate(dir, Path.GetFileName(path));
    }

    [RelayCommand]
    private void Copy() => Transfer(TransferKind.Copy);

    [RelayCommand]
    private void Move() => Transfer(TransferKind.Move);

    [RelayCommand]
    private void MakeDirectory()
    {
        var name = dialogs.Prompt("Chicken Commander", "New folder (directory):", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(ActivePanel.CurrentPath, name.Trim()));
            // "a\b\c" creates nested folders; put the cursor on the first level.
            var firstLevel = name.Trim().Split('\\', '/')[0];
            ActivePanel.Navigate(ActivePanel.CurrentPath, firstLevel, recordHistory: false);
            RefreshInactiveIfSameDirectory();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            dialogs.ShowError("Create folder", ex.Message);
        }
    }

    [RelayCommand]
    private void Delete() => DeleteSelection(toRecycleBin: true);

    [RelayCommand]
    private void DeletePermanently() => DeleteSelection(toRecycleBin: false);

    [RelayCommand]
    private void Rename()
    {
        var item = ActivePanel.CurrentItem;
        if (item is null || item.IsParentLink) return;
        var newName = dialogs.Prompt("Rename", $"Rename \"{item.Entry.Name}\" to:", item.Entry.Name, selectNameOnly: true);
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Entry.Name) return;
        var target = Path.Combine(ActivePanel.CurrentPath, newName.Trim());
        if (ShellFileOperation.Rename(dialogs.OwnerHandle, item.Entry.FullPath, target))
        {
            ActivePanel.Navigate(ActivePanel.CurrentPath, Path.GetFileName(target), recordHistory: false);
            RefreshInactiveIfSameDirectory();
        }
        else
        {
            ActivePanel.Refresh();
        }
    }

    // ---- Tools -------------------------------------------------------------------------------

    [RelayCommand]
    private void MultiRename()
    {
        var items = ActivePanel.SelectionOrCurrent();
        if (items.Count == 0) return;
        var sources = items.Select(i => new RenameSource(i.Entry.FullPath, i.Entry.IsDirectory, i.Entry.Modified)).ToList();
        if (dialogs.ShowMultiRename(sources))
        {
            ActivePanel.MarkAll(false);
            ActivePanel.Refresh();
            RefreshInactiveIfSameDirectory();
        }
        ActivePanel.RequestFocus();
    }

    [RelayCommand]
    private void ChangeAttributes()
    {
        var items = ActivePanel.SelectionOrCurrent();
        if (items.Count == 0) return;
        if (dialogs.ShowAttributes(items.Select(i => i.Entry).ToList()))
        {
            ActivePanel.Refresh();
            RefreshInactiveIfSameDirectory();
        }
        ActivePanel.RequestFocus();
    }

    [RelayCommand]
    private void ShowOperations() => OperationsWindowRequested?.Invoke();

    /// <summary>The view shows the operations window when asked.</summary>
    public event Action? OperationsWindowRequested;

    // ---- Tabs & history ----------------------------------------------------------------------

    [RelayCommand]
    private void NewTab() => ActivePanel.NewTab();

    [RelayCommand]
    private void CloseTab() => ActivePanel.CloseTab();

    [RelayCommand]
    private void NextTab() => ActivePanel.CycleTab(+1);

    [RelayCommand]
    private void PreviousTab() => ActivePanel.CycleTab(-1);

    /// <summary>Opens the folder under the cursor in a new tab (Ctrl+Up).</summary>
    [RelayCommand]
    private void OpenInNewTab()
    {
        var item = ActivePanel.CurrentItem;
        var path = item is { IsParentLink: false, Entry.IsDirectory: true } ? item.Entry.FullPath : ActivePanel.CurrentPath;
        ActivePanel.NewTab(path);
    }

    [RelayCommand]
    private void GoBack() => ActivePanel.GoBack();

    [RelayCommand]
    private void GoForward() => ActivePanel.GoForward();

    [RelayCommand]
    private void ShowHistory()
    {
        var panel = ActivePanel;
        var recent = panel.ActiveTab?.History.Recent() ?? [];
        if (recent.Count == 0) return;
        dialogs.ShowMenu(recent.Select(p => new MenuEntry(p, () => panel.Navigate(p))).ToList());
    }

    // ---- Hotlist -----------------------------------------------------------------------------

    public IReadOnlyList<HotlistEntry> Hotlist => settings.Hotlist;

    [RelayCommand]
    private void ShowHotlist()
    {
        var panel = ActivePanel;
        var entries = new List<MenuEntry>();
        foreach (var h in settings.Hotlist)
            entries.Add(new MenuEntry(h.Name, () => panel.Navigate(h.Path), h.Path));
        if (entries.Count > 0) entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry($"Add \"{panel.ActiveTab?.Title}\" to hotlist", AddToHotlist));
        if (settings.Hotlist.Count > 0)
        {
            var remove = settings.Hotlist.FirstOrDefault(h => string.Equals(h.Path, panel.CurrentPath, StringComparison.OrdinalIgnoreCase));
            if (remove is not null)
                entries.Add(new MenuEntry($"Remove \"{remove.Name}\" from hotlist", () => settings.Hotlist.Remove(remove)));
        }
        dialogs.ShowMenu(entries);
    }

    [RelayCommand]
    private void AddToHotlist()
    {
        var path = ActivePanel.CurrentPath;
        var suggested = ActivePanel.ActiveTab?.Title ?? path;
        var name = dialogs.Prompt("Directory hotlist", $"Name for {path}:", suggested);
        if (string.IsNullOrWhiteSpace(name)) return;
        settings.Hotlist.RemoveAll(h => string.Equals(h.Path, path, StringComparison.OrdinalIgnoreCase));
        settings.Hotlist.Add(new HotlistEntry(name.Trim(), path));
        settings.Hotlist.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }

    // ---- Panels ----------------------------------------------------------------------------

    [RelayCommand]
    private void Refresh()
    {
        ActivePanel.RefreshDrives();
        ActivePanel.Refresh();
    }

    [RelayCommand]
    private void SwapPanels()
    {
        var leftPath = Left.CurrentPath;
        var leftItem = Left.CurrentItem?.Entry.Name;
        Left.Navigate(Right.CurrentPath, Right.CurrentItem?.Entry.Name);
        Right.Navigate(leftPath, leftItem);
        SwitchPanel();
    }

    /// <summary>Opens the active panel's directory in the other panel too.</summary>
    [RelayCommand]
    private void TargetEqualsSource()
    {
        InactivePanel.Navigate(ActivePanel.CurrentPath, ActivePanel.CurrentItem?.Entry.Name);
        ActivePanel.RequestFocus();
    }

    [RelayCommand]
    private void ToggleHidden()
    {
        Left.ShowHidden = Right.ShowHidden = !Left.ShowHidden;
        Left.Refresh();
        Right.Refresh();
        OnPropertyChanged(nameof(ShowHidden));
    }

    // ---- Marking ---------------------------------------------------------------------------

    [RelayCommand]
    private void SelectGroup() => MarkGroup(true);

    [RelayCommand]
    private void UnselectGroup() => MarkGroup(false);

    [RelayCommand]
    private void SelectAll() => ActivePanel.MarkAll(true);

    [RelayCommand]
    private void UnselectAll() => ActivePanel.MarkAll(false);

    [RelayCommand]
    private void InvertSelection() => ActivePanel.InvertMarks();

    private void MarkGroup(bool mark)
    {
        var mask = dialogs.Prompt(mark ? "Select files" : "Unselect files", "Mask (e.g. *.txt;*.md | *.bak):", "*.*");
        if (!string.IsNullOrWhiteSpace(mask)) ActivePanel.MarkByMask(mask, mark);
        ActivePanel.RequestFocus();
    }

    // ---- Command line ----------------------------------------------------------------------

    [RelayCommand]
    private void ExecuteCommandLine()
    {
        var text = CommandLine.Trim();
        CommandLine = string.Empty;
        if (text.Length == 0) return;

        if (text.Equals("cd", StringComparison.OrdinalIgnoreCase) || text.StartsWith("cd ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("cd..", StringComparison.OrdinalIgnoreCase) || text.StartsWith(@"cd\", StringComparison.OrdinalIgnoreCase))
        {
            var arg = text[2..].Trim();
            if (arg.Length == 0) arg = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ActivePanel.Navigate(PathHelper.Resolve(ActivePanel.CurrentPath, arg));
            return;
        }

        var (file, args) = SplitCommand(text);
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = ActivePanel.CurrentPath });
        }
        catch (Exception)
        {
            // Not a program or document: let cmd.exe handle built-ins like "dir" or pipes.
            Process.Start(new ProcessStartInfo("cmd.exe", "/k " + text) { UseShellExecute = true, WorkingDirectory = ActivePanel.CurrentPath });
        }
    }

    public void NavigateActive(string input)
    {
        ActivePanel.Navigate(PathHelper.Resolve(ActivePanel.CurrentPath, input));
    }

    /// <summary>Enter on an item: enter folders, launch files with their associated program.</summary>
    public void OpenCurrent()
    {
        var item = ActivePanel.CurrentItem;
        if (item is null || ActivePanel.TryEnter(item)) return;
        ShellOpen(item.Entry.FullPath, null);
    }

    [RelayCommand]
    private void About() => dialogs.ShowAbout();

    // ---- Helpers ---------------------------------------------------------------------------

    private void ShellOpen(string file, string? args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args ?? string.Empty) { UseShellExecute = true, WorkingDirectory = ActivePanel.CurrentPath });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            dialogs.ShowError("Open", ex.Message);
        }
    }

    private void Transfer(TransferKind kind)
    {
        var items = ActivePanel.SelectionOrCurrent();
        if (items.Count == 0) return;

        var what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : $"{items.Count} file(s)/dir(s)";
        var initial = InactivePanel.CurrentPath;
        if (!initial.EndsWith('\\')) initial += "\\";
        if (dialogs.AskTransfer(kind, what, initial) is not { } answer || string.IsNullOrWhiteSpace(answer.Target)) return;

        var target = answer.Target.Trim();
        var keepSeparator = target.EndsWith('\\') || target.EndsWith('/');
        target = PathHelper.Resolve(ActivePanel.CurrentPath, target);
        if (keepSeparator && !target.EndsWith('\\')) target += "\\";

        var request = new TransferRequest(kind, items.Select(i => i.Entry.FullPath).ToList(), target, answer.Mode);
        foreach (var i in items) i.IsMarked = false;
        Operations.Enqueue(new TransferOperationViewModel(request, dialogs));
        ActivePanel.RequestFocus();
    }

    private void OnOperationCompleted(TransferOperationViewModel op)
    {
        Left.Refresh();
        Right.Refresh();
        if (op.State == OperationState.Failed) dialogs.ShowError(op.Title, op.DetailText);
    }

    private void DeleteSelection(bool toRecycleBin)
    {
        var items = ActivePanel.SelectionOrCurrent();
        if (items.Count == 0) return;
        var what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : $"{items.Count} file(s)/dir(s)";
        var question = toRecycleBin ? $"Delete {what} to the Recycle Bin?" : $"Permanently delete {what}?\nThis cannot be undone.";
        if (!dialogs.Confirm("Delete", question)) return;

        // Keep the cursor near where it was: on the first remaining item after the deleted block.
        var all = ActivePanel.Items;
        var deleted = items.ToHashSet();
        var lastIndex = items.Max(i => IndexOf(all, i));
        var next = all.Skip(lastIndex + 1).FirstOrDefault(i => !deleted.Contains(i))
                   ?? all.Take(lastIndex).LastOrDefault(i => !deleted.Contains(i));

        ShellFileOperation.Delete(dialogs.OwnerHandle, items.Select(i => i.Entry.FullPath), toRecycleBin);
        ActivePanel.Navigate(ActivePanel.CurrentPath, next?.Entry.Name, recordHistory: false);
        RefreshInactiveIfSameDirectory();
    }

    private void RefreshInactiveIfSameDirectory()
    {
        if (string.Equals(InactivePanel.CurrentPath, ActivePanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            InactivePanel.Refresh();
        ActivePanel.RequestFocus();
    }

    private static int IndexOf(IReadOnlyList<FileItemViewModel> list, FileItemViewModel item)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    private static (string File, string Args) SplitCommand(string text)
    {
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0) return (text[1..end], text[(end + 1)..].Trim());
        }
        var space = text.IndexOf(' ');
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }
}

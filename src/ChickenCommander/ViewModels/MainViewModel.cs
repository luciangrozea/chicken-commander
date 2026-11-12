using System.Diagnostics;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChickenCommander.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IDialogService dialogs;

    [ObservableProperty] private FilePanelViewModel activePanel = null!;
    [ObservableProperty] private string commandLine = string.Empty;

    public MainViewModel(IDialogService dialogs)
    {
        this.dialogs = dialogs;
        Left = new FilePanelViewModel();
        Right = new FilePanelViewModel();
        Left.ErrorOccurred += msg => dialogs.ShowError("Chicken Commander", msg);
        Right.ErrorOccurred += msg => dialogs.ShowError("Chicken Commander", msg);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        Left.Navigate(home);
        Right.Navigate(systemRoot);

        ActivePanel = Left;
    }

    public FilePanelViewModel Left { get; private set; }
    public FilePanelViewModel Right { get; private set; }
    public FilePanelViewModel InactivePanel => ActivePanel == Left ? Right : Left;

    /// <summary>Prompt shown left of the command line, e.g. "C:\Users&gt;".</summary>
    public string CommandPrompt => ActivePanel.CurrentPath + ">";

    partial void OnActivePanelChanged(FilePanelViewModel? oldValue, FilePanelViewModel newValue)
    {
        if (oldValue is not null) // null only for the first assignment in the constructor
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

    [RelayCommand]
    private void View() => LaunchCurrent(edit: false);

    [RelayCommand]
    private void Edit() => LaunchCurrent(edit: true);

    [RelayCommand]
    private void Copy() => Transfer(move: false);

    [RelayCommand]
    private void Move() => Transfer(move: true);

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
            ActivePanel.Navigate(ActivePanel.CurrentPath, firstLevel);
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
            ActivePanel.Navigate(ActivePanel.CurrentPath, Path.GetFileName(target));
            RefreshInactiveIfSameDirectory();
        }
        else
        {
            ActivePanel.Refresh();
        }
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

    public bool ShowHidden => Left.ShowHidden;

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

    private void LaunchCurrent(bool edit)
    {
        var item = ActivePanel.CurrentItem;
        if (item is null || item.Entry.IsDirectory) return;
        // The built-in viewer (Lister) arrives in v0.3; until then use the system's programs.
        if (edit) ShellOpen("notepad.exe", Quote(item.Entry.FullPath));
        else ShellOpen(item.Entry.FullPath, null);
    }

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

    private void Transfer(bool move)
    {
        var items = ActivePanel.SelectionOrCurrent();
        if (items.Count == 0) return;

        var what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : $"{items.Count} file(s)/dir(s)";
        var initial = Path.Combine(InactivePanel.CurrentPath, "");
        if (!initial.EndsWith('\\')) initial += "\\";
        var target = dialogs.Prompt(move ? "Move" : "Copy", $"{(move ? "Rename/move" : "Copy")} {what} to:", initial);
        if (string.IsNullOrWhiteSpace(target)) return;

        target = PathHelper.Resolve(ActivePanel.CurrentPath, target);
        var sources = items.Select(i => i.Entry.FullPath).ToList();
        if (move) ShellFileOperation.Move(dialogs.OwnerHandle, sources, target);
        else ShellFileOperation.Copy(dialogs.OwnerHandle, sources, target);

        // Unmark what was transferred, then reload both sides.
        foreach (var i in items) i.IsMarked = false;
        ActivePanel.Refresh();
        InactivePanel.Refresh();
        ActivePanel.RequestFocus();
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
        ActivePanel.Navigate(ActivePanel.CurrentPath, next?.Entry.Name);
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

using System.Collections.ObjectModel;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChickenCommander.ViewModels;

/// <summary>State of one of the two file panels: tabs, current directory, listing, cursor and marked items.</summary>
public partial class FilePanelViewModel : ObservableObject
{
    private readonly Dictionary<string, string> lastPathPerDrive = new(StringComparer.OrdinalIgnoreCase);
    private bool syncingDrive;
    private bool switchingTab;

    [ObservableProperty] private string currentPath = string.Empty;
    [ObservableProperty] private IReadOnlyList<FileItemViewModel> items = [];
    [ObservableProperty] private FileItemViewModel? currentItem;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private DriveItem? selectedDrive;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string freeSpaceText = string.Empty;
    [ObservableProperty] private PanelTab? activeTab;

    public FilePanelViewModel()
    {
        RefreshDrives();
    }

    public ObservableCollection<PanelTab> Tabs { get; } = [];

    public IReadOnlyList<DriveItem> Drives { get; private set; } = [];

    public SortOrder Sort { get; private set; } = SortOrder.Default;

    public bool ShowHidden { get; set; }

    /// <summary>Raised when the view should move keyboard focus to an item (after navigation or reload).</summary>
    public event Action<FileItemViewModel?>? FocusRequested;

    /// <summary>Raised when a directory cannot be opened.</summary>
    public event Action<string>? ErrorOccurred;

    public IEnumerable<FileItemViewModel> MarkedItems => Items.Where(i => i.IsMarked);

    /// <summary>The marked items, or the item under the cursor when nothing is marked. Never contains "..".</summary>
    public IReadOnlyList<FileItemViewModel> SelectionOrCurrent()
    {
        var marked = MarkedItems.ToList();
        if (marked.Count > 0) return marked;
        return CurrentItem is { IsParentLink: false } current ? [current] : [];
    }

    public void RefreshDrives()
    {
        Drives = DriveItem.Enumerate();
        OnPropertyChanged(nameof(Drives));
    }

    // ---- Settings ----------------------------------------------------------------------------

    /// <summary>Restores tabs from settings; missing folders are dropped, and <paramref name="fallback"/> is used if none remain.</summary>
    public void Restore(PanelSettings settings, string fallback)
    {
        Tabs.Clear();
        foreach (var path in settings.Tabs.Where(Directory.Exists)) Tabs.Add(new PanelTab(path));
        if (Tabs.Count == 0) Tabs.Add(new PanelTab(fallback));
        var active = Tabs[Math.Clamp(settings.ActiveTab, 0, Tabs.Count - 1)];
        switchingTab = true;
        ActiveTab = active;
        switchingTab = false;
        if (!Navigate(active.Path)) Navigate(fallback);
    }

    public PanelSettings ToSettings() => new()
    {
        Tabs = Tabs.Select(t => t.Path).ToList(),
        ActiveTab = ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab),
    };

    // ---- Navigation --------------------------------------------------------------------------

    /// <summary>Loads <paramref name="path"/> and puts the cursor on <paramref name="selectName"/> if present.</summary>
    public bool Navigate(string path, string? selectName = null, IReadOnlySet<string>? keepMarked = null, bool recordHistory = true)
    {
        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = DirectoryReader.Read(path, Sort, ShowHidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ErrorOccurred?.Invoke(ex.Message);
            return false;
        }

        var full = Path.GetFullPath(path);
        var list = entries.Select(e => new FileItemViewModel(e)).ToList();
        if (keepMarked is { Count: > 0 })
        {
            foreach (var item in list.Where(i => keepMarked.Contains(i.Entry.Name)))
                item.IsMarked = true;
        }
        foreach (var item in list) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileItemViewModel.IsMarked)) UpdateStatus();
        };

        if (ActiveTab is null)
        {
            var tab = new PanelTab(full);
            Tabs.Add(tab);
            switchingTab = true;
            ActiveTab = tab;
            switchingTab = false;
        }
        ActiveTab.Path = full;
        if (recordHistory) ActiveTab.History.Visit(full);

        CurrentPath = full;
        Items = list;
        CurrentItem = (selectName is null ? null : list.FirstOrDefault(i => string.Equals(i.Entry.Name, selectName, StringComparison.OrdinalIgnoreCase)))
                      ?? list.FirstOrDefault();

        var root = Path.GetPathRoot(full) ?? full;
        lastPathPerDrive[root] = full;
        syncingDrive = true;
        SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
        syncingDrive = false;
        FreeSpaceText = SelectedDrive?.FreeSpaceText() ?? string.Empty;

        UpdateStatus();
        FocusRequested?.Invoke(CurrentItem);
        return true;
    }

    /// <summary>Reloads the current directory, keeping the cursor and marks where possible.</summary>
    public void Refresh()
    {
        var marked = MarkedItems.Select(i => i.Entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = CurrentItem?.Entry.Name;
        if (!Directory.Exists(CurrentPath))
        {
            // Directory vanished (deleted elsewhere, drive removed): walk up until something exists.
            var dir = CurrentPath;
            while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
            Navigate(dir ?? Path.GetPathRoot(Environment.SystemDirectory)!);
            return;
        }
        Navigate(CurrentPath, name, marked, recordHistory: false);
    }

    public void GoUp()
    {
        var parent = Directory.GetParent(CurrentPath);
        if (parent is null) return;
        Navigate(parent.FullName, Path.GetFileName(CurrentPath.TrimEnd('\\')));
    }

    public void GoBack()
    {
        if (ActiveTab?.History.GoBack() is { } path) Navigate(path, recordHistory: false);
    }

    public void GoForward()
    {
        if (ActiveTab?.History.GoForward() is { } path) Navigate(path, recordHistory: false);
    }

    /// <summary>Enters a directory; returns false for files so the caller can launch them.</summary>
    public bool TryEnter(FileItemViewModel item)
    {
        if (item.IsParentLink)
        {
            GoUp();
            return true;
        }
        if (!item.Entry.IsDirectory) return false;
        Navigate(item.Entry.FullPath);
        return true;
    }

    public void SortBy(SortColumn column)
    {
        Sort = Sort.Toggle(column);
        Refresh();
    }

    // ---- Tabs --------------------------------------------------------------------------------

    /// <summary>Opens a new tab (on <paramref name="path"/>, or a copy of the current folder) right after the active one.</summary>
    public void NewTab(string? path = null)
    {
        var tab = new PanelTab(path ?? CurrentPath);
        var index = ActiveTab is null ? Tabs.Count : Tabs.IndexOf(ActiveTab) + 1;
        Tabs.Insert(index, tab);
        ActiveTab = tab;
    }

    public void CloseTab(PanelTab? tab = null)
    {
        tab ??= ActiveTab;
        if (tab is null || Tabs.Count <= 1) return;
        var index = Tabs.IndexOf(tab);
        var wasActive = tab == ActiveTab;
        Tabs.Remove(tab);
        if (wasActive) ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
    }

    public void CycleTab(int delta)
    {
        if (Tabs.Count <= 1 || ActiveTab is null) return;
        var index = (Tabs.IndexOf(ActiveTab) + delta + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[index];
    }

    partial void OnActiveTabChanged(PanelTab? oldValue, PanelTab? newValue)
    {
        if (switchingTab || newValue is null) return;
        if (oldValue is not null) oldValue.CursorName = CurrentItem?.Entry.Name;
        if (!string.Equals(newValue.Path, CurrentPath, StringComparison.OrdinalIgnoreCase) || oldValue != newValue)
        {
            if (!Navigate(newValue.Path, newValue.CursorName, recordHistory: newValue.History.Current is null))
            {
                // Folder of that tab is gone: fall back to its nearest existing parent.
                var dir = newValue.Path;
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (dir is not null) Navigate(dir);
            }
        }
    }

    // ---- Marking -----------------------------------------------------------------------------

    public void ToggleMark(FileItemViewModel? item)
    {
        if (item is null || item.IsParentLink) return;
        item.IsMarked = !item.IsMarked;
    }

    public void MarkByMask(string mask, bool mark)
    {
        var matcher = new WildcardMask(mask);
        // Masks act on files only; folders are marked individually (Insert/Space) or with Ctrl+A.
        foreach (var item in Items.Where(i => !i.Entry.IsDirectory && matcher.IsMatch(i.Entry.Name)))
            item.IsMarked = mark;
    }

    public void MarkAll(bool mark)
    {
        foreach (var item in Items.Where(i => !i.IsParentLink)) item.IsMarked = mark;
    }

    public void InvertMarks()
    {
        foreach (var item in Items.Where(i => !i.IsParentLink && !i.Entry.IsDirectory)) item.IsMarked = !item.IsMarked;
    }

    public void RequestFocus() => FocusRequested?.Invoke(CurrentItem);

    partial void OnSelectedDriveChanged(DriveItem? value)
    {
        if (syncingDrive || value is null) return;
        if (string.Equals(Path.GetPathRoot(CurrentPath), value.Root, StringComparison.OrdinalIgnoreCase)) return;
        var target = lastPathPerDrive.TryGetValue(value.Root, out var last) && Directory.Exists(last) ? last : value.Root;
        if (!Navigate(target))
        {
            // Drive not ready (empty card reader, disconnected share): put the combo back.
            syncingDrive = true;
            SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Root, Path.GetPathRoot(CurrentPath), StringComparison.OrdinalIgnoreCase));
            syncingDrive = false;
        }
    }

    private void UpdateStatus()
    {
        long markedBytes = 0, totalBytes = 0;
        int markedFiles = 0, totalFiles = 0, markedDirs = 0, totalDirs = 0;
        foreach (var item in Items)
        {
            if (item.IsParentLink) continue;
            if (item.Entry.IsDirectory)
            {
                totalDirs++;
                if (item.IsMarked) markedDirs++;
            }
            else
            {
                totalFiles++;
                totalBytes += item.Entry.Size;
                if (item.IsMarked)
                {
                    markedFiles++;
                    markedBytes += item.Entry.Size;
                }
            }
        }
        StatusText = $"{SizeFormatter.Human(markedBytes)} / {SizeFormatter.Human(totalBytes)} in {markedFiles} / {totalFiles} file(s), {markedDirs} / {totalDirs} dir(s)";
    }
}

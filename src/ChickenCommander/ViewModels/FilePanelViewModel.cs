using System.Collections.ObjectModel;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Remote;
using ChickenCommander.Core.Settings;
using ChickenCommander.Core.Vfs;
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

    private readonly VirtualFileSystem vfs;

    public FilePanelViewModel(VirtualFileSystem vfs)
    {
        this.vfs = vfs;
        RefreshDrives();
    }

    /// <summary>Local disk, inside an archive, or on a server: decides which operations are possible.</summary>
    public LocationKind Location { get; private set; }

    public bool IsLocal => Location == LocationKind.Local;

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
        // Server tabs are not restored (the connection is closed); archive tabs are, if the archive still exists.
        // (Only the archive file is checked here: reading a big archive's listing would delay the start.)
        foreach (var path in settings.Tabs.Where(t => !t.Contains("://", StringComparison.Ordinal)
                                                      && (ArchiveProvider.TrySplit(t, out _, out _) || vfs.DirectoryExists(t))))
            Tabs.Add(new PanelTab(path));
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
        // Going somewhere else drops an archive that is still being read.
        busy?.Cancel();

        // The first visit to an archive reads its whole listing, which takes seconds (or minutes) for big
        // .7z / .tar.gz files: do that in the background and show the archive when it is ready.
        if (ArchiveProvider.TrySplit(path, out var archivePath, out _) && !vfs.Archives.IsCached(archivePath))
        {
            _ = NavigateWhenIndexedAsync(archivePath, path, selectName, keepMarked, recordHistory);
            return true;
        }

        IReadOnlyList<FileEntry> entries;
        string full;
        try
        {
            full = vfs.Normalize(path);
            entries = vfs.Read(full, Sort, ShowHidden);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Disk errors, broken archives, password-protected archives, dropped connections…
            ErrorOccurred?.Invoke(ex.Message);
            return false;
        }
        Location = vfs.KindOf(full);
        var list = entries.Select(e => new FileItemViewModel(e)).ToList();
        if (keepMarked is { Count: > 0 })
        {
            foreach (var item in list.Where(i => keepMarked.Contains(i.Entry.Name)))
                item.IsMarked = true;
        }
        foreach (var item in list) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FileItemViewModel.IsMarked) && !changingMarks) UpdateStatus();
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

        syncingDrive = true;
        if (Location == LocationKind.Remote)
        {
            SelectedDrive = null;
            RemotePath.TryParsePanelPath(full, out var scheme, out var name, out _);
            FreeSpaceText = vfs.Sessions.Find(name) is { } s
                ? $"{scheme.ToUpperInvariant()}  {s.Connection.User}@{s.Connection.Host}:{s.Connection.EffectivePort}"
                : name;
        }
        else
        {
            var root = Path.GetPathRoot(full) ?? full;
            if (Location == LocationKind.Local) lastPathPerDrive[root] = full;
            SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Root, root, StringComparison.OrdinalIgnoreCase));
            FreeSpaceText = (SelectedDrive?.FreeSpaceText() ?? string.Empty) + (Location == LocationKind.Archive ? "   ·  inside archive (read-only)" : "");
        }
        syncingDrive = false;

        UpdateStatus();
        FocusRequested?.Invoke(CurrentItem);
        return true;
    }

    // ---- Background work (reading big archives) -------------------------------------------------

    /// <summary>What the panel is busy with ("Reading archive…"), or null. The view shows it over the list.</summary>
    [ObservableProperty] private string? busyText;

    private CancellationTokenSource? busy;

    public bool IsBusy => BusyText is not null;

    /// <summary>Stops reading an archive (Esc or the Cancel button); the panel stays where it was.</summary>
    public void CancelBusy() => busy?.Cancel();

    private async Task NavigateWhenIndexedAsync(string archivePath, string path, string? selectName,
        IReadOnlySet<string>? keepMarked, bool recordHistory)
    {
        var cts = busy = new CancellationTokenSource();
        var size = new FileInfo(archivePath).Length;
        BusyText = $"Reading {Path.GetFileName(archivePath)} ({SizeFormatter.Human(size)})…\nPress Esc to cancel.";
        string? error = null;
        try
        {
            await Task.Run(() => vfs.Archives.GetIndex(archivePath, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Broken or unsupported archive, password-protected headers, disk errors, not enough memory…
            error = ex.Message;
        }
        finally
        {
            if (busy == cts)
            {
                busy = null;
                BusyText = null;
            }
            cts.Dispose();
        }
        if (cts.IsCancellationRequested && error is null)
        {
            StartupFallback(archivePath);
            FocusRequested?.Invoke(CurrentItem);
            return;
        }
        if (error is not null)
        {
            ErrorOccurred?.Invoke(error);
            StartupFallback(archivePath);
            return;
        }
        if (!Navigate(path, selectName, keepMarked, recordHistory)) StartupFallback(archivePath);
    }

    /// <summary>When an archive tab restored at start cannot be shown, the panel shows the archive's folder instead of nothing.</summary>
    private void StartupFallback(string archivePath)
    {
        if (CurrentPath.Length == 0 && Path.GetDirectoryName(archivePath) is { } folder) Navigate(folder, Path.GetFileName(archivePath));
    }

    /// <summary>Reloads the current directory, keeping the cursor and marks where possible.</summary>
    public void Refresh()
    {
        var marked = MarkedItems.Select(i => i.Entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = CurrentItem?.Entry.Name;
        if (!vfs.DirectoryExists(CurrentPath))
        {
            // Directory vanished (deleted elsewhere, drive removed, connection closed): walk up until something exists.
            var dir = CurrentPath;
            while (!string.IsNullOrEmpty(dir) && !vfs.DirectoryExists(dir)) dir = vfs.GetParent(dir);
            Navigate(dir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return;
        }
        Navigate(CurrentPath, name, marked, recordHistory: false);
    }

    public void GoUp()
    {
        var parent = vfs.GetParent(CurrentPath);
        if (parent is null) return;
        var trimmed = CurrentPath.TrimEnd('\\', '/');
        Navigate(parent, trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..]);
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
        if (!item.Entry.IsDirectory)
        {
            // Enter on an archive opens it like a folder.
            if (Location == LocationKind.Local && ArchiveProvider.IsArchiveFile(item.Entry.FullPath))
                return Navigate(item.Entry.FullPath);
            return false;
        }
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

    /// <summary>Closes every tab except <paramref name="keep"/>, which becomes the active one.</summary>
    public void CloseOtherTabs(PanelTab keep)
    {
        if (!Tabs.Contains(keep)) return;
        ActiveTab = keep;
        foreach (var tab in Tabs.Where(t => t != keep).ToList()) Tabs.Remove(tab);
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
                while (!string.IsNullOrEmpty(dir) && !vfs.DirectoryExists(dir)) dir = vfs.GetParent(dir);
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
        ChangeMarks(() =>
        {
            foreach (var item in Items.Where(i => !i.Entry.IsDirectory && matcher.IsMatch(i.Entry.Name)))
                item.IsMarked = mark;
        });
    }

    public void MarkAll(bool mark) => ChangeMarks(() =>
    {
        foreach (var item in Items.Where(i => !i.IsParentLink)) item.IsMarked = mark;
    });

    public void InvertMarks() => ChangeMarks(() =>
    {
        foreach (var item in Items.Where(i => !i.IsParentLink && !i.Entry.IsDirectory)) item.IsMarked = !item.IsMarked;
    });

    /// <summary>
    /// Shift+click / Shift+arrows: marks everything between <paramref name="from"/> and <paramref name="to"/>.
    /// Without <paramref name="keepOthers"/> (plain Shift) the marks outside the range are removed, like in Explorer;
    /// with it (Ctrl+Shift) the range is added to the existing marks.
    /// </summary>
    public void MarkRange(FileItemViewModel from, FileItemViewModel to, bool keepOthers)
    {
        var a = IndexOfItem(from);
        var b = IndexOfItem(to);
        if (a < 0 || b < 0) return;
        if (a > b) (a, b) = (b, a);
        ChangeMarks(() =>
        {
            for (var i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                if (i >= a && i <= b) item.IsMarked = !item.IsParentLink;
                else if (!keepOthers) item.IsMarked = false;
            }
        });
    }

    /// <summary>
    /// Drag rectangle: rows <paramref name="first"/>..<paramref name="last"/> are marked, every other row goes back
    /// to how it was when the drag started (<paramref name="markedBefore"/>: empty for a plain drag, the old marks with Ctrl).
    /// <paramref name="first"/> greater than <paramref name="last"/> means the rectangle touches no row.
    /// </summary>
    public void ApplyMarquee(int first, int last, IReadOnlySet<FileItemViewModel> markedBefore)
    {
        ChangeMarks(() =>
        {
            for (var i = 0; i < Items.Count; i++)
            {
                var item = Items[i];
                var inside = i >= first && i <= last && !item.IsParentLink;
                item.IsMarked = inside || markedBefore.Contains(item);
            }
        });
    }

    public int IndexOfItem(FileItemViewModel item)
    {
        for (var i = 0; i < Items.Count; i++)
            if (ReferenceEquals(Items[i], item)) return i;
        return -1;
    }

    private bool changingMarks;

    /// <summary>Changes many marks with one status-line update at the end (instead of one per item).</summary>
    private void ChangeMarks(Action change)
    {
        changingMarks = true;
        try { change(); }
        finally
        {
            changingMarks = false;
            UpdateStatus();
        }
    }

    public void RequestFocus() => FocusRequested?.Invoke(CurrentItem);

    partial void OnSelectedDriveChanged(DriveItem? value)
    {
        if (syncingDrive || value is null) return;
        if (Location != LocationKind.Remote && string.Equals(Path.GetPathRoot(CurrentPath), value.Root, StringComparison.OrdinalIgnoreCase)) return;
        var target = lastPathPerDrive.TryGetValue(value.Root, out var last) && Directory.Exists(last) ? last : value.Root;
        if (!Navigate(target))
        {
            // Drive not ready (empty card reader, disconnected share): put the combo back.
            syncingDrive = true;
            SelectedDrive = Location == LocationKind.Remote ? null
                : Drives.FirstOrDefault(d => string.Equals(d.Root, Path.GetPathRoot(CurrentPath), StringComparison.OrdinalIgnoreCase));
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

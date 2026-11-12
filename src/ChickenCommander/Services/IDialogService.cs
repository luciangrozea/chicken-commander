using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;

namespace ChickenCommander.Services;

/// <summary>An entry of a popup menu (hotlist, history). A null action is a separator.</summary>
public sealed record MenuEntry(string Header, Action? Action, string? Hint = null)
{
    public static MenuEntry Separator => new("-", null);
}

public interface IDialogService
{
    /// <summary>Window handle used as owner for native shell dialogs.</summary>
    IntPtr OwnerHandle { get; }

    /// <summary>Asks for a line of text; returns null when cancelled.</summary>
    string? Prompt(string title, string message, string initialValue, bool selectNameOnly = false);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    void ShowAbout();

    /// <summary>The F5/F6 dialog: target path plus overwrite policy. Null when cancelled.</summary>
    (string Target, OverwriteMode Mode)? AskTransfer(TransferKind kind, string description, string initialTarget);

    ConflictChoice ResolveConflict(ConflictInfo conflict);

    ErrorChoice AskOnError(string path, string message);

    /// <summary>Opens the multi-rename tool; returns true if anything was renamed.</summary>
    bool ShowMultiRename(IReadOnlyList<RenameSource> items);

    /// <summary>Opens the attributes dialog; returns true if anything changed.</summary>
    bool ShowAttributes(IReadOnlyList<FileEntry> items);

    /// <summary>Shows a popup menu over the active panel.</summary>
    void ShowMenu(IReadOnlyList<MenuEntry> entries);

    /// <summary>Opens the built-in viewer on <paramref name="files"/>, starting at <paramref name="index"/>.</summary>
    void ShowLister(IReadOnlyList<string> files, int index);

    /// <summary>Opens (or brings back) the non-modal file search window.</summary>
    void ShowSearch(string startFolder);

    void ShowCompare(string left, string right);

    /// <summary>Opens the directory synchronization tool; returns true if files were copied.</summary>
    bool ShowSync(string left, string right);
}

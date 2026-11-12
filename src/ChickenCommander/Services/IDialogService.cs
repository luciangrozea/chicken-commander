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

    /// <summary>Alt+F5: archive file name and format. Null when cancelled.</summary>
    (string Path, Core.Vfs.PackFormat Format)? AskPack(string description, string defaultPath);

    /// <summary>
    /// Ctrl+N: the connection manager. Edits <paramref name="connections"/> in place and returns the
    /// connection to open with the password typed for it (null password = use the saved one).
    /// </summary>
    (Core.Remote.RemoteConnection Connection, string? Password)? ShowConnections(List<Core.Remote.RemoteConnection> connections);

    /// <summary>Asks for a password (hidden input). Null when cancelled.</summary>
    string? AskPassword(string title, string message);

    /// <summary>Checksum algorithm and whether to write one checksum file per source file.</summary>
    (Core.Tools.ChecksumKind Kind, bool OnePerFile)? AskChecksum(int fileCount);

    void ShowVerifyResults(string checksumFile, IReadOnlyList<Core.Tools.VerifyResult> results);

    /// <summary>Trust prompts for SSH host keys and TLS certificates (may be called from any thread).</summary>
    Core.Remote.IRemoteTrust Trust { get; }
}

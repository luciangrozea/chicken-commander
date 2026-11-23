using System.Diagnostics;
using ChickenCommander.Core.Settings;

namespace ChickenCommander.Commands;

/// <summary>A command the user can run from the keyboard or the button bar.</summary>
/// <param name="Glyph">Segoe Fluent Icons / MDL2 Assets character for the button bar.</param>
public sealed record CommandDefinition(string Id, string Category, string Title, string[] DefaultGestures, string Glyph, Action<MainWindow> Execute);

/// <summary>
/// Every command in one table: default shortcuts, button-bar icons and what they do.
/// Keys whose meaning depends on context (Enter, Backspace, Space, Esc with the command line) stay built in.
/// </summary>
public static class CommandRegistry
{
    public static IReadOnlyList<CommandDefinition> All { get; } =
    [
        // Panels & navigation
        new("SwitchPanel", "Panels", "Switch panel", ["Tab", "Shift+Tab"], "", w => w.Vm.SwitchPanelCommand.Execute(null)),
        new("GoUp", "Panels", "Parent folder", ["Ctrl+PageUp"], "", w => w.Vm.ActivePanel.GoUp()),
        new("OpenCurrent", "Panels", "Open folder / archive under cursor", ["Ctrl+PageDown"], "", w => w.Vm.OpenCurrent()),
        new("Root", "Panels", "Drive root", ["Ctrl+Backslash"], "", w => w.GoToRoot()),
        new("FirstItem", "Panels", "First item", ["Home"], "", w => w.ActivePanelView.MoveCursorToEdge(last: false)),
        new("LastItem", "Panels", "Last item", ["End"], "", w => w.ActivePanelView.MoveCursorToEdge(last: true)),
        new("LeftDrive", "Panels", "Choose drive (left)", ["Alt+F1"], "", w => w.LeftPanel.OpenDriveList()),
        new("RightDrive", "Panels", "Choose drive (right)", ["Alt+F2"], "", w => w.RightPanel.OpenDriveList()),
        new("EditPath", "Panels", "Edit path", ["Ctrl+L"], "", w => w.ActivePanelView.FocusPathBox()),
        new("GoBack", "Panels", "Back", ["Alt+Left"], "", w => w.Vm.GoBackCommand.Execute(null)),
        new("GoForward", "Panels", "Forward", ["Alt+Right"], "", w => w.Vm.GoForwardCommand.Execute(null)),
        new("ShowHistory", "Panels", "Folder history", ["Alt+Down"], "", w => w.Vm.ShowHistoryCommand.Execute(null)),
        new("ShowHotlist", "Panels", "Directory hotlist", ["Ctrl+D"], "", w => w.Vm.ShowHotlistCommand.Execute(null)),
        new("AddToHotlist", "Panels", "Add folder to hotlist", [], "", w => w.Vm.AddToHotlistCommand.Execute(null)),
        new("Refresh", "Panels", "Refresh", ["Ctrl+R"], "", w => w.Vm.RefreshCommand.Execute(null)),
        new("SwapPanels", "Panels", "Swap panels", ["Ctrl+U"], "", w => w.Vm.SwapPanelsCommand.Execute(null)),
        new("TargetEqualsSource", "Panels", "Target = source", ["Ctrl+E"], "", w => w.Vm.TargetEqualsSourceCommand.Execute(null)),
        new("ToggleHidden", "Panels", "Show hidden files", ["Ctrl+H"], "", w => w.Vm.ToggleHiddenCommand.Execute(null)),
        new("ToggleQuickView", "Panels", "Quick View panel", ["Ctrl+Q"], "", w => w.Vm.ToggleQuickViewCommand.Execute(null)),

        // Tabs
        new("NewTab", "Tabs", "New tab", ["Ctrl+T"], "", w => w.Vm.NewTabCommand.Execute(null)),
        new("CloseTab", "Tabs", "Close tab", ["Ctrl+W"], "", w => w.Vm.CloseTabCommand.Execute(null)),
        new("NextTab", "Tabs", "Next tab", ["Ctrl+Tab"], "", w => w.Vm.NextTabCommand.Execute(null)),
        new("PreviousTab", "Tabs", "Previous tab", ["Ctrl+Shift+Tab"], "", w => w.Vm.PreviousTabCommand.Execute(null)),
        new("OpenInNewTab", "Tabs", "Open folder in new tab", ["Ctrl+Up"], "", w => w.Vm.OpenInNewTabCommand.Execute(null)),

        // Files
        new("View", "Files", "View (Lister)", ["F3"], "", w => w.Vm.ViewCommand.Execute(null)),
        new("Edit", "Files", "Edit", ["F4"], "", w => w.Vm.EditCommand.Execute(null)),
        new("Copy", "Files", "Copy", ["F5"], "", w => w.Vm.CopyCommand.Execute(null)),
        new("Move", "Files", "Move / rename", ["F6"], "", w => w.Vm.MoveCommand.Execute(null)),
        new("Rename", "Files", "Rename in place", ["Shift+F6"], "", w => w.Vm.RenameCommand.Execute(null)),
        new("MakeDirectory", "Files", "New folder", ["F7"], "", w => w.Vm.MakeDirectoryCommand.Execute(null)),
        new("Delete", "Files", "Delete", ["F8", "Delete"], "", w => w.Vm.DeleteCommand.Execute(null)),
        new("DeletePermanently", "Files", "Delete permanently", ["Shift+F8", "Shift+Delete"], "", w => w.Vm.DeletePermanentlyCommand.Execute(null)),
        new("MultiRename", "Files", "Multi-Rename tool", ["Ctrl+M"], "", w => w.Vm.MultiRenameCommand.Execute(null)),
        new("ChangeAttributes", "Files", "Change attributes", ["Ctrl+I"], "", w => w.Vm.ChangeAttributesCommand.Execute(null)),
        new("Pack", "Files", "Pack", ["Alt+F5"], "", w => w.Vm.PackCommand.Execute(null)),
        new("Unpack", "Files", "Unpack", ["Alt+F9"], "", w => w.Vm.UnpackCommand.Execute(null)),
        new("CreateChecksums", "Files", "Create checksums", [], "", w => w.Vm.CreateChecksumsCommand.Execute(null)),
        new("VerifyChecksums", "Files", "Verify checksums", [], "", w => w.Vm.VerifyChecksumsCommand.Execute(null)),
        new("CompareFiles", "Files", "Compare by content", ["Ctrl+Shift+C"], "", w => w.Vm.CompareFilesCommand.Execute(null)),
        new("CopyToClipboard", "Files", "Copy to clipboard", ["Ctrl+C"], "", w => w.Vm.CopyToClipboardCommand.Execute(null)),
        new("CutToClipboard", "Files", "Cut to clipboard", ["Ctrl+X"], "", w => w.Vm.CutToClipboardCommand.Execute(null)),
        new("Paste", "Files", "Paste from clipboard", ["Ctrl+V"], "", w => w.Vm.PasteCommand.Execute(null)),
        new("ContextMenu", "Files", "Explorer context menu", ["Apps", "Shift+F10"], "", w => w.ShowContextMenuAtCursor()),
        new("ShowOperations", "Files", "File operations window", ["Ctrl+Shift+O"],"", w => w.Vm.ShowOperationsCommand.Execute(null)),

        // Marking
        new("MarkItem", "Marking", "Mark item and move down", ["Insert"], "", w => w.MarkAndMoveDown()),
        new("SelectGroup", "Marking", "Mark by mask", ["NumPlus"], "", w => w.Vm.SelectGroupCommand.Execute(null)),
        new("UnselectGroup", "Marking", "Unmark by mask", ["NumMinus"], "", w => w.Vm.UnselectGroupCommand.Execute(null)),
        new("InvertSelection", "Marking", "Invert marks", ["NumMultiply"], "", w => w.Vm.InvertSelectionCommand.Execute(null)),
        new("SelectAll", "Marking", "Mark all", ["Ctrl+A", "Ctrl+NumPlus"], "", w => w.Vm.SelectAllCommand.Execute(null)),
        new("UnselectAll", "Marking", "Unmark all", ["Ctrl+Shift+A", "Ctrl+NumMinus"], "", w => w.Vm.UnselectAllCommand.Execute(null)),

        // Tools & network
        new("Search", "Tools", "Find files", ["Alt+F7"], "", w => w.Vm.SearchCommand.Execute(null)),
        new("SyncDirectories", "Tools", "Synchronize directories", ["Ctrl+Shift+S"], "", w => w.Vm.SyncDirectoriesCommand.Execute(null)),
        new("Connect", "Tools", "Connect to server", ["Ctrl+N"], "", w => w.Vm.ConnectCommand.Execute(null)),
        new("Disconnect", "Tools", "Disconnect", ["Ctrl+Shift+N"], "", w => w.Vm.DisconnectCommand.Execute(null)),
        new("CommandPrompt", "Tools", "Command prompt here", [], "", w => w.StartInWorkingDirectory("cmd.exe", null)),
        new("Explorer", "Tools", "Open in Explorer", [], "", w => w.StartInWorkingDirectory("explorer.exe", ".")),
        new("Settings", "Tools", "Settings", ["Ctrl+Comma"], "", w => w.ShowSettings()),
        new("Help", "Tools", "Keyboard shortcuts", ["F1"], "", w => w.ShowShortcuts()),
    ];

    private static readonly Dictionary<string, CommandDefinition> ById = All.ToDictionary(c => c.Id);

    public static CommandDefinition? Find(string? id) => id is not null && ById.TryGetValue(id, out var c) ? c : null;

    public static IReadOnlyDictionary<string, string[]> DefaultGestures { get; } = All.ToDictionary(c => c.Id, c => c.DefaultGestures);

    public static List<ButtonBarItem> DefaultButtonBar() =>
    [
        ButtonBarItem.ForCommand("Refresh"),
        ButtonBarItem.Separator,
        ButtonBarItem.ForCommand("GoBack"),
        ButtonBarItem.ForCommand("GoForward"),
        ButtonBarItem.ForCommand("NewTab"),
        ButtonBarItem.ForCommand("ShowHotlist"),
        ButtonBarItem.Separator,
        ButtonBarItem.ForCommand("ToggleQuickView"),
        ButtonBarItem.ForCommand("Search"),
        ButtonBarItem.ForCommand("MultiRename"),
        ButtonBarItem.ForCommand("CompareFiles"),
        ButtonBarItem.ForCommand("SyncDirectories"),
        ButtonBarItem.Separator,
        ButtonBarItem.ForCommand("Pack"),
        ButtonBarItem.ForCommand("Unpack"),
        ButtonBarItem.ForCommand("Connect"),
        ButtonBarItem.Separator,
        ButtonBarItem.ForCommand("CommandPrompt"),
        ButtonBarItem.ForCommand("Explorer"),
        ButtonBarItem.ForCommand("Settings"),
    ];

    /// <summary>Default icon for program buttons.</summary>
    public const string ProgramGlyph = "";

    internal static void Start(string file, string? arguments, string workingDirectory) =>
        Process.Start(new ProcessStartInfo(file, arguments ?? string.Empty) { UseShellExecute = true, WorkingDirectory = workingDirectory });
}

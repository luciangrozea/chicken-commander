using System.Runtime.InteropServices;
using ChickenCommander.Core.Operations;
using ChickenCommander.Core.Remote;
using ChickenCommander.Core.Vfs;
using ChickenCommander.Services;
using CommunityToolkit.Mvvm.Input;

namespace ChickenCommander.ViewModels;

/// <summary>Ctrl+C / Ctrl+X / Ctrl+V with the Windows clipboard (works together with Explorer).</summary>
public partial class MainViewModel
{
    [RelayCommand]
    private void CopyToClipboard() => PutOnClipboard(cut: false);

    [RelayCommand]
    private void CutToClipboard() => PutOnClipboard(cut: true);

    [RelayCommand]
    private void Paste() => PasteInto(ActivePanel.CurrentPath);

    /// <summary>Puts the marked files (or the one under the cursor) on the clipboard.</summary>
    public void PutOnClipboard(bool cut)
    {
        var title = cut ? "Cut" : "Copy";
        var items = ActivePanel.SelectionOrCurrent();
        if (items.Count == 0) return;
        if (!ActivePanel.IsLocal)
        {
            dialogs.ShowError(title, "The clipboard works with files on local and network drives. Use F5 to copy files out of an archive or server.");
            return;
        }
        try
        {
            FileClipboard.Put(items.Select(i => i.Entry.FullPath).ToList(), cut);
        }
        catch (ExternalException ex)
        {
            dialogs.ShowError(title, "The clipboard is in use by another program. Try again.\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// Pastes the files from the clipboard into <paramref name="folder"/>: copied files are copied, cut files are moved.
    /// Runs in the background queue like F5 / F6, with the usual "file exists" questions.
    /// </summary>
    public void PasteInto(string folder)
    {
        FileClipboard.Content? content;
        try
        {
            content = FileClipboard.Get();
        }
        catch (ExternalException ex)
        {
            dialogs.ShowError("Paste", "The clipboard is in use by another program. Try again.\n\n" + ex.Message);
            return;
        }
        if (content is null)
        {
            dialogs.ShowError("Paste", "There are no files on the clipboard. Copy (Ctrl+C) or cut (Ctrl+X) files first.");
            return;
        }

        var sources = content.Paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (sources.Count == 0)
        {
            dialogs.ShowError("Paste", "The files on the clipboard no longer exist.");
            return;
        }

        var kind = content.IsCut ? TransferKind.Move : TransferKind.Copy;
        switch (Vfs.KindOf(folder))
        {
            case LocationKind.Archive:
                dialogs.ShowError("Paste", "Archives are read-only here. To create an archive, mark the files and press Alt+F5 (Pack).");
                return;
            case LocationKind.Remote:
            {
                var (session, remoteDir) = Vfs.Remote.Resolve(folder);
                var move = content.IsCut;
                Operations.Enqueue(new TransferOperationViewModel(TransferOperationViewModel.Describe(move ? "Upload (move)" : "Upload", sources, folder), dialogs,
                    (cb, pause, ct) => RemoteTransfer.UploadAsync(session, sources, remoteDir, cb, OverwriteMode.Ask, move, pause, ct)));
                break;
            }
            default:
            {
                var target = folder.EndsWith('\\') ? folder : folder + "\\";
                Operations.Enqueue(new TransferOperationViewModel(new TransferRequest(kind, sources, target, OverwriteMode.Ask), dialogs));
                break;
            }
        }
        // Cut files are moved once; pasting them again would find nothing, so the clipboard is emptied like in Explorer.
        if (content.IsCut) FileClipboard.Clear();
        ActivePanel.RequestFocus();
    }
}

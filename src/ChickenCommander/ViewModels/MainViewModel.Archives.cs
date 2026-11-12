using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;
using ChickenCommander.Core.Remote;
using ChickenCommander.Core.Settings;
using ChickenCommander.Core.Tools;
using ChickenCommander.Core.Vfs;
using ChickenCommander.Services;
using CommunityToolkit.Mvvm.Input;

namespace ChickenCommander.ViewModels;

/// <summary>Archives, servers and checksums (v0.4).</summary>
public partial class MainViewModel
{
    private const string ArchiveReadOnlyMessage = "Archives are opened read-only.";

    /// <summary>Where temporary copies (F3 / Enter inside archives and on servers) are kept; removed on exit.</summary>
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "ChickenCommander", Environment.ProcessId.ToString());

    public VirtualFileSystem Vfs { get; } = new();

    /// <summary>A local folder for programs and relative paths: the panel's folder, or the user's home when the panel is virtual.</summary>
    private string WorkingDirectory =>
        ActivePanel.IsLocal ? ActivePanel.CurrentPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string WorkingFolder => WorkingDirectory;

    public AppSettings Settings => settings;

    /// <summary>After the Settings dialog: reload the panels so list-related options take effect.</summary>
    public void ApplySettingsChanges()
    {
        Left.Refresh();
        Right.Refresh();
    }

    private bool RequireLocal(string feature)
    {
        if (ActivePanel.IsLocal) return true;
        dialogs.ShowError(feature, $"{feature} works on local and network drives, not inside archives or on servers.");
        return false;
    }

    /// <summary>Extracts or downloads one file to a temporary folder so it can be viewed or opened.</summary>
    private async Task<string?> MaterializeAsync(FilePanelViewModel panel, FileEntry entry)
    {
        var folder = Path.Combine(tempRoot, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var callbacks = new SilentCallbacks();
        try
        {
            if (panel.Location == LocationKind.Archive && ArchiveProvider.TrySplit(entry.FullPath, out var archive, out var inner))
            {
                await Task.Run(() => ArchiveTools.ExtractAsync(archive, [inner], folder, Vfs.Archives.GetIndex(archive), callbacks, OverwriteMode.OverwriteAll));
            }
            else if (panel.Location == LocationKind.Remote)
            {
                var (session, remote) = Vfs.Remote.Resolve(entry.FullPath);
                await RemoteTransfer.DownloadAsync(session, [remote], folder, callbacks, OverwriteMode.OverwriteAll);
            }
            var path = Path.Combine(folder, entry.Name);
            if (File.Exists(path)) return path;
            dialogs.ShowError("Open", callbacks.LastError ?? $"Could not get a copy of \"{entry.Name}\".");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            dialogs.ShowError("Open", ex.Message);
        }
        return null;
    }

    /// <summary>Called on exit: closes connections and deletes temporary copies.</summary>
    public void Cleanup()
    {
        Vfs.Sessions.CloseAll();
        try
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A viewer may still hold a file open; Windows cleans %TEMP% eventually.
        }
    }

    // ---- Pack / unpack ---------------------------------------------------------------------

    /// <summary>Alt+F5: packs the selection into a new archive (default: next to the other panel's folder).</summary>
    [RelayCommand]
    private void Pack()
    {
        var items = ActivePanel.SelectionOrCurrent();
        if (items.Count == 0 || !RequireLocal("Pack")) return;
        var baseName = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].Entry.Name.TrimEnd('\\')) : new DirectoryInfo(ActivePanel.CurrentPath).Name;
        if (string.IsNullOrEmpty(baseName)) baseName = "archive";
        var targetDir = InactivePanel.IsLocal ? InactivePanel.CurrentPath : ActivePanel.CurrentPath;
        var what = items.Count == 1 ? $"\"{items[0].Entry.Name}\"" : $"{items.Count} file(s)/dir(s)";
        if (dialogs.AskPack(what, Path.Combine(targetDir, baseName + ".zip")) is not { } answer) return;

        var archivePath = PathHelper.Resolve(ActivePanel.CurrentPath, answer.Path);
        if (File.Exists(archivePath) && !dialogs.Confirm("Pack", $"\"{archivePath}\" already exists. Replace it?")) return;
        var sources = items.Select(i => i.Entry.FullPath).ToList();
        foreach (var i in items) i.IsMarked = false;
        Operations.Enqueue(new TransferOperationViewModel(TransferOperationViewModel.Describe("Pack", sources, archivePath), dialogs,
            (cb, pause, ct) => ArchiveTools.PackAsync(sources, archivePath, answer.Format, cb, pause: pause, ct: ct)));
        ActivePanel.RequestFocus();
    }

    /// <summary>Alt+F9: extracts the archive under the cursor (or the marked archives) into the other panel's folder.</summary>
    [RelayCommand]
    private void Unpack()
    {
        if (!RequireLocal("Unpack")) return;
        var archives = ActivePanel.SelectionOrCurrent().Where(i => !i.Entry.IsDirectory && ArchiveProvider.IsArchiveFile(i.Entry.FullPath)).ToList();
        if (archives.Count == 0)
        {
            dialogs.ShowError("Unpack", "Put the cursor on an archive (zip, 7z, rar, tar, tar.gz…) or mark several.");
            return;
        }
        var initial = (InactivePanel.IsLocal ? InactivePanel.CurrentPath : ActivePanel.CurrentPath).TrimEnd('\\') + "\\";
        var what = archives.Count == 1 ? $"\"{archives[0].Entry.Name}\"" : $"{archives.Count} archives";
        var answer = dialogs.Prompt("Unpack", $"Unpack {what} to:", initial);
        if (string.IsNullOrWhiteSpace(answer)) return;
        var target = PathHelper.Resolve(ActivePanel.CurrentPath, answer);

        foreach (var a in archives)
        {
            var archive = a.Entry.FullPath;
            a.IsMarked = false;
            Operations.Enqueue(new TransferOperationViewModel(TransferOperationViewModel.Describe("Unpack", [archive], target), dialogs,
                (cb, pause, ct) => ArchiveTools.ExtractAsync(archive, [""], target, Vfs.Archives.GetIndex(archive, ct), cb, OverwriteMode.Ask, pause, ct)));
        }
        ActivePanel.RequestFocus();
    }

    // ---- Servers ---------------------------------------------------------------------------

    /// <summary>Ctrl+N: pick or edit a saved connection and open it in the active panel.</summary>
    [RelayCommand]
    private async Task Connect()
    {
        if (dialogs.ShowConnections(settings.Connections) is not { } choice) return;
        var connection = choice.Connection;
        var password = choice.Password ?? SecretStore.Unprotect(connection.EncryptedPassword);
        if (password is null && string.IsNullOrEmpty(connection.PrivateKeyFile))
        {
            password = dialogs.AskPassword(connection.Name, $"Password for {connection.User}@{connection.Host}:");
            if (password is null) return;
        }

        var session = new RemoteSession(connection, () => CreateClient(connection.Name, password));
        try
        {
            // Connect and read the start folder off the UI thread (may ask about host keys / certificates).
            var start = connection.InitialDirectory;
            await Task.Run(() => session.Use(c => c.DirectoryExists(start)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            session.Dispose();
            dialogs.ShowError("Connect", $"Could not connect to {connection.Host}:\n{ex.Message}");
            return;
        }

        Vfs.Sessions.Add(session);
        ActivePanel.Navigate(connection.ToPanelPath(connection.InitialDirectory));
        ActivePanel.RequestFocus();
    }

    /// <summary>Closes the server connection shown in the active panel and goes back to a local folder.</summary>
    [RelayCommand]
    private void Disconnect()
    {
        if (Vfs.Sessions.ForPath(ActivePanel.CurrentPath) is not { } session)
        {
            dialogs.ShowError("Disconnect", "The active panel is not showing a server.");
            return;
        }
        var name = session.Connection.Name;
        Vfs.Sessions.Close(name);
        foreach (var panel in new[] { Left, Right })
        {
            if (Vfs.Remote.CanHandle(panel.CurrentPath) && RemotePath.TryParsePanelPath(panel.CurrentPath, out _, out var n, out _) && n == name)
                panel.Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
    }

    private IRemoteClient CreateClient(string connectionName, string? password)
    {
        // Always read the latest saved record, so a fingerprint accepted a minute ago is used.
        var connection = settings.Connections.First(c => c.Name == connectionName);
        return connection.Protocol == RemoteProtocol.Sftp
            ? new SftpRemoteClient(connection, password, dialogs.Trust, fp => UpdateConnection(connectionName, c => c with { HostKeyFingerprint = fp }))
            : new FtpRemoteClient(connection, password, dialogs.Trust, hash => UpdateConnection(connectionName, c => c with { AcceptedCertificate = hash }));
    }

    private void UpdateConnection(string name, Func<RemoteConnection, RemoteConnection> change)
    {
        lock (settings.Connections)
        {
            var i = settings.Connections.FindIndex(c => c.Name == name);
            if (i >= 0) settings.Connections[i] = change(settings.Connections[i]);
        }
    }

    // ---- Checksums -------------------------------------------------------------------------

    /// <summary>Creates checksum file(s) for the selected files (MD5 / SHA-1 / SHA-256 / SHA-512 / CRC32 SFV).</summary>
    [RelayCommand]
    private void CreateChecksums()
    {
        if (!RequireLocal("Create checksums")) return;
        var files = ActivePanel.SelectionOrCurrent().Where(i => !i.Entry.IsDirectory).Select(i => i.Entry.FullPath).ToList();
        if (files.Count == 0)
        {
            dialogs.ShowError("Create checksums", "Select one or more files.");
            return;
        }
        if (dialogs.AskChecksum(files.Count) is not { } answer) return;

        var folder = ActivePanel.CurrentPath;
        var kind = answer.Kind;
        var outputs = answer.OnePerFile
            ? files.Select(f => (Output: f + Checksums.Extension(kind), Files: (IReadOnlyList<string>)[f])).ToList()
            : [(Path.Combine(folder, (files.Count == 1 ? Path.GetFileName(files[0]) : new DirectoryInfo(folder).Name) + Checksums.Extension(kind)), files)];

        Operations.Enqueue(new TransferOperationViewModel(TransferOperationViewModel.Describe("Checksum " + kind.ToString().ToUpperInvariant(), files, folder), dialogs,
            async (cb, pause, ct) =>
            {
                var total = files.Sum(f => new FileInfo(f).Length);
                long done = 0;
                var count = 0;
                foreach (var (output, group) in outputs)
                {
                    var lines = new List<string>();
                    foreach (var file in group)
                    {
                        await pause.WaitIfPausedAsync(ct);
                        var size = new FileInfo(file).Length;
                        var start = done;
                        var hash = await Checksums.ComputeAsync(file, kind, n => cb.Report(new TransferProgress(file, n, size, start + n, total, count, files.Count)), ct);
                        lines.Add(Checksums.FormatLine(kind, hash, Path.GetRelativePath(Path.GetDirectoryName(output)!, file).Replace('\\', '/')));
                        done += size;
                        count++;
                    }
                    await File.WriteAllLinesAsync(output, lines, ct);
                }
                return new TransferResult(count, 0, 0, false);
            }));
    }

    /// <summary>Verifies the checksum file under the cursor (.md5 / .sha1 / .sha256 / .sha512 / .sfv).</summary>
    [RelayCommand]
    private async Task VerifyChecksums()
    {
        if (!RequireLocal("Verify checksums")) return;
        var item = ActivePanel.CurrentItem;
        if (item is null || item.Entry.IsDirectory || Checksums.KindFromExtension(item.Entry.FullPath) is null)
        {
            dialogs.ShowError("Verify checksums", "Put the cursor on a checksum file (.md5, .sha1, .sha256, .sha512 or .sfv).");
            return;
        }
        try
        {
            var path = item.Entry.FullPath;
            var results = await Task.Run(() => Checksums.VerifyAsync(path));
            dialogs.ShowVerifyResults(path, results);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            dialogs.ShowError("Verify checksums", ex.Message);
        }
    }

    /// <summary>Callbacks for background helper jobs: overwrite silently, remember the last error.</summary>
    private sealed class SilentCallbacks : ITransferCallbacks
    {
        public string? LastError { get; private set; }
        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => Task.FromResult(ConflictChoice.Overwrite);

        public Task<ErrorChoice> HandleErrorAsync(string path, Exception error)
        {
            LastError = error.Message;
            return Task.FromResult(ErrorChoice.Cancel);
        }

        public void Report(TransferProgress progress) { }
    }
}

using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;

namespace ChickenCommander.Core.Remote;

/// <summary>Downloads and uploads with the same progress / conflict / error behaviour as local copies.</summary>
public static class RemoteTransfer
{
    private sealed record Item(string Source, string Destination, bool IsDirectory, long Size, DateTime Modified);

    /// <summary>Copies remote files and folders into a local folder. With <paramref name="deleteSource"/> it is a move.</summary>
    public static async Task<TransferResult> DownloadAsync(
        RemoteSession session, IReadOnlyList<string> remotePaths, string localDirectory, ITransferCallbacks callbacks,
        OverwriteMode mode = OverwriteMode.Ask, bool deleteSource = false, PauseController? pause = null, CancellationToken ct = default)
    {
        using var client = await Task.Run(session.OpenClient, ct).ConfigureAwait(false);
        var work = new List<Item>();
        foreach (var remote in remotePaths)
        {
            var name = RemotePath.Name(remote);
            var dest = Path.Combine(localDirectory, name);
            if (client.DirectoryExists(remote)) CollectRemote(client, remote, dest, work, ct);
            else if (client.GetFileInfo(remote) is { } info) work.Add(new Item(remote, dest, false, info.Size, info.Modified));
        }

        var state = new Progress(work);
        foreach (var item in work)
        {
            if (ct.IsCancellationRequested) return state.Result(true);
            if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(item.Destination);
                continue;
            }

            var destination = item.Destination;
            if (File.Exists(destination))
            {
                var local = new FileInfo(destination);
                var (action, newMode) = await Decide(mode, item, local.Length, local.LastWriteTime, destination, callbacks).ConfigureAwait(false);
                mode = newMode;
                if (action == ConflictChoice.Cancel) return state.Result(true);
                if (action == ConflictChoice.Skip) { state.Skip(item); continue; }
                if (action == ConflictChoice.Rename) destination = Path.Combine(Path.GetDirectoryName(destination)!, PathHelper.UniqueName(Path.GetDirectoryName(destination)!, Path.GetFileName(destination)));
                else if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
            }

            var outcome = await Run(item, callbacks, state, ct, () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var ok = false;
                try
                {
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        client.Download(item.Source, output, n => state.Report(callbacks, item, n), ct);
                    if (item.Modified > DateTime.MinValue) File.SetLastWriteTime(destination, item.Modified);
                    ok = true;
                    if (deleteSource) client.DeleteFile(item.Source);
                }
                finally
                {
                    if (!ok && File.Exists(destination)) File.Delete(destination);
                }
            }).ConfigureAwait(false);
            if (outcome == ErrorChoice.Cancel) return state.Result(true);
        }

        if (deleteSource)
        {
            // Remove remote folders that were fully moved (deepest first); leave anything that still has content.
            foreach (var dir in work.Where(w => w.IsDirectory).Select(w => w.Source).OrderByDescending(p => p.Length))
            {
                try { if (client.List(dir, ct).Count == 0) client.DeleteDirectory(dir); }
                catch (Exception ex) when (IsTransferError(ex)) { }
            }
        }
        return state.Result(false);
    }

    /// <summary>Copies local files and folders into a remote folder. With <paramref name="deleteSource"/> it is a move.</summary>
    public static async Task<TransferResult> UploadAsync(
        RemoteSession session, IReadOnlyList<string> localPaths, string remoteDirectory, ITransferCallbacks callbacks,
        OverwriteMode mode = OverwriteMode.Ask, bool deleteSource = false, PauseController? pause = null, CancellationToken ct = default)
    {
        using var client = await Task.Run(session.OpenClient, ct).ConfigureAwait(false);
        var work = new List<Item>();
        foreach (var local in localPaths)
        {
            var dest = RemotePath.Combine(remoteDirectory, Path.GetFileName(local.TrimEnd('\\')));
            if (Directory.Exists(local))
            {
                work.Add(new Item(local, dest, true, 0, default));
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var info in new DirectoryInfo(local).EnumerateFileSystemInfos("*", options))
                {
                    var rel = Path.GetRelativePath(local, info.FullName).Replace('\\', '/');
                    work.Add(info is FileInfo f
                        ? new Item(f.FullName, RemotePath.Combine(dest, rel), false, f.Length, f.LastWriteTime)
                        : new Item(info.FullName, RemotePath.Combine(dest, rel), true, 0, default));
                }
            }
            else if (File.Exists(local))
            {
                var f = new FileInfo(local);
                work.Add(new Item(local, dest, false, f.Length, f.LastWriteTime));
            }
        }

        var state = new Progress(work);
        foreach (var item in work)
        {
            if (ct.IsCancellationRequested) return state.Result(true);
            if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
            if (item.IsDirectory)
            {
                var outcomeDir = await Run(item, callbacks, state, ct, () =>
                {
                    if (!client.DirectoryExists(item.Destination)) client.CreateDirectory(item.Destination);
                }, countAsFile: false).ConfigureAwait(false);
                if (outcomeDir == ErrorChoice.Cancel) return state.Result(true);
                continue;
            }

            var destination = item.Destination;
            if (client.GetFileInfo(destination) is { } existing)
            {
                var (action, newMode) = await Decide(mode, item, existing.Size, existing.Modified, destination, callbacks).ConfigureAwait(false);
                mode = newMode;
                if (action == ConflictChoice.Cancel) return state.Result(true);
                if (action == ConflictChoice.Skip) { state.Skip(item); continue; }
                if (action == ConflictChoice.Rename) destination = UniqueRemote(client, destination);
            }

            var outcome = await Run(item, callbacks, state, ct, () =>
            {
                using (var input = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    client.Upload(input, destination, n => state.Report(callbacks, item, n), ct);
                if (deleteSource)
                {
                    File.SetAttributes(item.Source, FileAttributes.Normal);
                    File.Delete(item.Source);
                }
            }).ConfigureAwait(false);
            if (outcome == ErrorChoice.Cancel) return state.Result(true);
        }

        if (deleteSource)
        {
            foreach (var dir in work.Where(w => w.IsDirectory).Select(w => w.Source).OrderByDescending(p => p.Length))
            {
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                catch (IOException) { }
            }
        }
        return state.Result(false);
    }

    private static void CollectRemote(IRemoteClient client, string remoteDir, string localDir, List<Item> work, CancellationToken ct)
    {
        work.Add(new Item(remoteDir, localDir, true, 0, default));
        foreach (var e in client.List(remoteDir, ct))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(localDir, e.Name);
            if (e.IsDirectory) CollectRemote(client, e.FullPath, dest, work, ct);
            else work.Add(new Item(e.FullPath, dest, false, e.Size, e.Modified));
        }
    }

    private static async Task<(ConflictChoice Action, OverwriteMode Mode)> Decide(
        OverwriteMode mode, Item item, long existingSize, DateTime existingModified, string destination, ITransferCallbacks callbacks)
    {
        switch (mode)
        {
            case OverwriteMode.OverwriteAll: return (ConflictChoice.Overwrite, mode);
            case OverwriteMode.SkipAll: return (ConflictChoice.Skip, mode);
            case OverwriteMode.RenameAll: return (ConflictChoice.Rename, mode);
            case OverwriteMode.OverwriteOlder: return (existingModified < item.Modified ? ConflictChoice.Overwrite : ConflictChoice.Skip, mode);
        }
        var choice = await callbacks.ResolveConflictAsync(new ConflictInfo(item.Source, destination, item.Size, item.Modified, existingSize, existingModified)).ConfigureAwait(false);
        return choice switch
        {
            ConflictChoice.OverwriteAll => (ConflictChoice.Overwrite, OverwriteMode.OverwriteAll),
            ConflictChoice.SkipAll => (ConflictChoice.Skip, OverwriteMode.SkipAll),
            ConflictChoice.RenameAll => (ConflictChoice.Rename, OverwriteMode.RenameAll),
            ConflictChoice.OverwriteOlderAll => (existingModified < item.Modified ? ConflictChoice.Overwrite : ConflictChoice.Skip, OverwriteMode.OverwriteOlder),
            _ => (choice, mode),
        };
    }

    /// <summary>Runs one step on a worker thread with Retry / Skip / Cancel on failure.</summary>
    private static async Task<ErrorChoice> Run(Item item, ITransferCallbacks callbacks, Progress state, CancellationToken ct, Action step, bool countAsFile = true)
    {
        while (true)
        {
            try
            {
                await Task.Run(step, ct).ConfigureAwait(false);
                if (countAsFile) state.Done(callbacks, item);
                return ErrorChoice.Skip; // "continue with the next item"
            }
            catch (OperationCanceledException)
            {
                return ErrorChoice.Cancel;
            }
            catch (Exception ex) when (IsTransferError(ex))
            {
                var answer = await callbacks.HandleErrorAsync(item.Source, ex).ConfigureAwait(false);
                if (answer == ErrorChoice.Retry) continue;
                state.Error(item);
                return answer;
            }
        }
    }

    private static bool IsTransferError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException
            or System.Net.Sockets.SocketException || ex.GetType().Namespace?.StartsWith("Renci", StringComparison.Ordinal) == true
            || ex.GetType().Namespace?.StartsWith("FluentFTP", StringComparison.Ordinal) == true;

    private static string UniqueRemote(IRemoteClient client, string path)
    {
        var dir = RemotePath.Parent(path) ?? "/";
        var name = RemotePath.Name(path);
        var ext = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = RemotePath.Combine(dir, $"{stem} ({i}){ext}");
            if (client.GetFileInfo(candidate) is null) return candidate;
        }
    }

    /// <summary>Counters shared by the loops above.</summary>
    private sealed class Progress(List<Item> work)
    {
        private readonly long bytesTotal = work.Where(w => !w.IsDirectory).Sum(w => w.Size);
        private readonly int filesTotal = work.Count(w => !w.IsDirectory);
        private long bytesDone;
        private int done, skipped, errors;

        public void Report(ITransferCallbacks cb, Item item, long fileDone) =>
            cb.Report(new TransferProgress(item.Source, fileDone, item.Size, bytesDone + fileDone, bytesTotal, done, filesTotal));

        public void Done(ITransferCallbacks cb, Item item)
        {
            done++;
            bytesDone += item.Size;
            cb.Report(new TransferProgress(item.Source, item.Size, item.Size, bytesDone, bytesTotal, done, filesTotal));
        }

        public void Skip(Item item) { skipped++; bytesDone += item.Size; }

        public void Error(Item item) { errors++; bytesDone += item.Size; }

        public TransferResult Result(bool cancelled) => new(done, skipped, errors, cancelled);
    }
}

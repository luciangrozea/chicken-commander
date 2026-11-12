using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.Core.Operations;

/// <summary>
/// Copy / move engine: recursive, buffered, with progress, pause, cancel, conflict and error callbacks.
/// Moves within one volume are plain renames; across volumes they are copy + delete.
/// </summary>
public static class FileTransfer
{
    private const int BufferSize = 1024 * 1024;

    private sealed record WorkItem(string Source, string Destination, bool IsDirectory, long Size);

    /// <summary>
    /// Works out where each source goes. A target that ends with a separator, is an existing folder,
    /// or receives several sources is a folder; otherwise a single source is copied to that exact name.
    /// </summary>
    public static IReadOnlyList<(string Source, string Destination)> MapDestinations(IReadOnlyList<string> sources, string target)
    {
        var intoFolder = target.EndsWith('\\') || target.EndsWith('/') || Directory.Exists(target) || sources.Count > 1;
        var root = Path.TrimEndingDirectorySeparator(target);
        return sources
            .Select(s => (s, intoFolder ? Path.Combine(root, Path.GetFileName(Path.TrimEndingDirectorySeparator(s))) : root))
            .ToList();
    }

    public static async Task<TransferResult> RunAsync(
        TransferRequest request,
        ITransferCallbacks callbacks,
        PauseController? pause = null,
        CancellationToken ct = default)
    {
        var mode = request.Overwrite;
        int done = 0, skipped = 0, errors = 0;
        var pairs = MapDestinations(request.Sources, request.Target);

        foreach (var (source, destination) in pairs)
        {
            if (Directory.Exists(source) && IsSameOrInside(destination, source) && !PathsEqual(destination, source))
                throw new InvalidOperationException($"Cannot {request.Kind.ToString().ToLowerInvariant()} \"{source}\" into itself.");
        }

        // Fast path: a move inside one volume to a free name is a single rename, whatever its size.
        var work = new List<WorkItem>();
        var moveRoots = new List<string>();
        foreach (var (source, rawDestination) in pairs)
        {
            ct.ThrowIfCancellationRequested();
            var destination = rawDestination;
            var isDir = Directory.Exists(source);
            if (!isDir && !File.Exists(source)) { errors++; continue; }

            if (PathsEqual(source, destination))
            {
                if (request.Kind == TransferKind.Move) { skipped++; continue; }
                // Copy onto itself: make "name (2).ext" next to it, like Explorer's "- Copy".
                destination = Path.Combine(Path.GetDirectoryName(destination)!, PathHelper.UniqueName(Path.GetDirectoryName(destination)!, Path.GetFileName(destination)));
            }

            if (request.Kind == TransferKind.Move && SameVolume(source, destination) && !File.Exists(destination) && !Directory.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (isDir) Directory.Move(source, destination);
                else File.Move(source, destination);
                done++;
                continue;
            }

            if (isDir)
            {
                work.Add(new WorkItem(source, destination, true, 0));
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var info in new DirectoryInfo(source).EnumerateFileSystemInfos("*", options))
                {
                    var relative = Path.GetRelativePath(source, info.FullName);
                    var dest = Path.Combine(destination, relative);
                    work.Add(info is DirectoryInfo
                        ? new WorkItem(info.FullName, dest, true, 0)
                        : new WorkItem(info.FullName, dest, false, ((FileInfo)info).Length));
                }
                if (request.Kind == TransferKind.Move) moveRoots.Add(source);
            }
            else
            {
                work.Add(new WorkItem(source, destination, false, new FileInfo(source).Length));
            }
        }

        var filesTotal = work.Count(w => !w.IsDirectory) + done;
        var bytesTotal = work.Sum(w => w.Size);
        long bytesDone = 0;
        var buffer = new byte[BufferSize];

        foreach (var item in work)
        {
            if (ct.IsCancellationRequested) return new TransferResult(done, skipped, errors, Cancelled: true);
            if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);

            if (item.IsDirectory)
            {
                if (File.Exists(item.Destination))
                {
                    errors++;
                    var choice = await callbacks.HandleErrorAsync(item.Destination, new IOException("A file with this name already exists."));
                    if (choice == ErrorChoice.Cancel) return new TransferResult(done, skipped, errors, true);
                    continue;
                }
                Directory.CreateDirectory(item.Destination);
                continue;
            }

            var destination = item.Destination;
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                var decision = await DecideConflictAsync(item, destination, mode, callbacks).ConfigureAwait(false);
                mode = decision.NewMode;
                switch (decision.Action)
                {
                    case ConflictChoice.Cancel:
                        return new TransferResult(done, skipped, errors, Cancelled: true);
                    case ConflictChoice.Skip:
                        skipped++;
                        bytesDone += item.Size;
                        continue;
                    case ConflictChoice.Rename:
                        destination = Path.Combine(Path.GetDirectoryName(destination)!, PathHelper.UniqueName(Path.GetDirectoryName(destination)!, Path.GetFileName(destination)));
                        break;
                }
            }

            while (true)
            {
                try
                {
                    var fileStart = bytesDone;
                    await CopyFileAsync(item.Source, destination, item.Size, buffer,
                        fileDone => callbacks.Report(new TransferProgress(item.Source, fileDone, item.Size, fileStart + fileDone, bytesTotal, done, filesTotal)),
                        pause, ct).ConfigureAwait(false);
                    if (request.Kind == TransferKind.Move) DeleteFile(item.Source);
                    bytesDone = fileStart + item.Size;
                    done++;
                    callbacks.Report(new TransferProgress(item.Source, item.Size, item.Size, bytesDone, bytesTotal, done, filesTotal));
                    break;
                }
                catch (OperationCanceledException)
                {
                    return new TransferResult(done, skipped, errors, Cancelled: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    var choice = await callbacks.HandleErrorAsync(item.Source, ex).ConfigureAwait(false);
                    if (choice == ErrorChoice.Retry) continue;
                    errors++;
                    bytesDone += item.Size;
                    if (choice == ErrorChoice.Cancel) return new TransferResult(done, skipped, errors, Cancelled: true);
                    break;
                }
            }
        }

        // Cross-volume move: remove source folders that are now empty (anything skipped stays put).
        foreach (var root in moveRoots) RemoveEmptyDirectories(root);

        return new TransferResult(done, skipped, errors, Cancelled: false);
    }

    private static async Task<(ConflictChoice Action, OverwriteMode NewMode)> DecideConflictAsync(
        WorkItem item, string destination, OverwriteMode mode, ITransferCallbacks callbacks)
    {
        if (Directory.Exists(destination))
            return (ConflictChoice.Rename, mode); // a folder is in the way: never overwrite folders with files

        var destInfo = new FileInfo(destination);
        var srcInfo = new FileInfo(item.Source);

        switch (mode)
        {
            case OverwriteMode.OverwriteAll: return (ConflictChoice.Overwrite, mode);
            case OverwriteMode.SkipAll: return (ConflictChoice.Skip, mode);
            case OverwriteMode.RenameAll: return (ConflictChoice.Rename, mode);
            case OverwriteMode.OverwriteOlder:
                return (destInfo.LastWriteTimeUtc < srcInfo.LastWriteTimeUtc ? ConflictChoice.Overwrite : ConflictChoice.Skip, mode);
        }

        var choice = await callbacks.ResolveConflictAsync(new ConflictInfo(
            item.Source, destination, srcInfo.Length, srcInfo.LastWriteTime, destInfo.Length, destInfo.LastWriteTime)).ConfigureAwait(false);

        return choice switch
        {
            ConflictChoice.OverwriteAll => (ConflictChoice.Overwrite, OverwriteMode.OverwriteAll),
            ConflictChoice.SkipAll => (ConflictChoice.Skip, OverwriteMode.SkipAll),
            ConflictChoice.RenameAll => (ConflictChoice.Rename, OverwriteMode.RenameAll),
            ConflictChoice.OverwriteOlderAll => (destInfo.LastWriteTimeUtc < srcInfo.LastWriteTimeUtc ? ConflictChoice.Overwrite : ConflictChoice.Skip, OverwriteMode.OverwriteOlder),
            _ => (choice, mode),
        };
    }

    private static async Task CopyFileAsync(string source, string destination, long size, byte[] buffer,
        Action<long> report, PauseController? pause, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            var attrs = File.GetAttributes(destination);
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(destination, attrs & ~FileAttributes.ReadOnly);
        }

        var completed = false;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                if (size > 0) output.SetLength(size); // pre-allocate: less fragmentation, early "disk full"
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    report(total);
                    if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
                }
                if (output.Length != total) output.SetLength(total); // source shrank while copying
            }

            var src = new FileInfo(source);
            File.SetCreationTimeUtc(destination, src.CreationTimeUtc);
            File.SetLastWriteTimeUtc(destination, src.LastWriteTimeUtc);
            File.SetAttributes(destination, src.Attributes);
            completed = true;
        }
        finally
        {
            if (!completed) TryDelete(destination); // never leave half-written files behind
        }
    }

    private static void DeleteFile(string path)
    {
        var attrs = File.GetAttributes(path);
        if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) DeleteFile(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root)) RemoveEmptyDirectories(dir);
        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string candidate, string folder)
    {
        var c = Path.GetFullPath(candidate).TrimEnd('\\') + "\\";
        var f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return c.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}

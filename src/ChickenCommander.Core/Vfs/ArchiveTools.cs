using System.Formats.Tar;
using System.IO.Compression;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;

namespace ChickenCommander.Core.Vfs;

public enum PackFormat { Zip, Tar, TarGz }

public static class ArchiveTools
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Extracts the selected items (files or folders inside one archive) into <paramref name="targetDirectory"/>.
    /// Items keep their path relative to the folder they were selected in, like a normal copy.
    /// The archive is read sequentially once, which is fast even for solid 7z/RAR archives.
    /// </summary>
    public static async Task<TransferResult> ExtractAsync(
        string archivePath, IReadOnlyList<string> innerItems, string targetDirectory, ArchiveIndex index,
        ITransferCallbacks callbacks, OverwriteMode mode = OverwriteMode.Ask, PauseController? pause = null, CancellationToken ct = default)
    {
        // Selected item "docs\2025" extracts its contents to target\2025\...; whole archive = [""].
        var selections = innerItems.Select(ArchiveIndex.NormalizeKey).ToList();
        var bases = selections.ToDictionary(s => s, s => s.Contains('\\') ? s[..s.LastIndexOf('\\')] : string.Empty, StringComparer.OrdinalIgnoreCase);

        var wanted = selections.SelectMany(s => index.FilesUnder(s)).ToList();
        var bytesTotal = wanted.Sum(f => f.Size);
        var filesTotal = wanted.Count;
        long bytesDone = 0;
        int done = 0, skipped = 0, errors = 0;
        var root = Path.GetFullPath(targetDirectory).TrimEnd('\\') + "\\";
        var buffer = new byte[BufferSize];

        // Folders (also empty ones) are created up front.
        foreach (var s in selections)
        {
            if (!index.FolderExists(s)) continue;
            var rel = s.Length == 0 ? string.Empty : s[(bases[s].Length == 0 ? 0 : bases[s].Length + 1)..];
            Directory.CreateDirectory(Path.Combine(root, rel));
        }

        foreach (var (entry, openEntry) in ArchiveReading.Entries(archivePath))
        {
            ct.ThrowIfCancellationRequested();
            if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
            var key = ArchiveIndex.NormalizeKey(entry.Key);
            var selection = selections.FirstOrDefault(s => s.Length == 0 || key.Equals(s, StringComparison.OrdinalIgnoreCase) || key.StartsWith(s + "\\", StringComparison.OrdinalIgnoreCase));
            if (selection is null || key.Length == 0) continue;

            var baseDir = bases[selection];
            var relative = baseDir.Length == 0 ? key : key[(baseDir.Length + 1)..];
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                // "Zip slip": an entry like "..\..\Windows\evil.dll" must never escape the target folder.
                errors++;
                continue;
            }

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            if (File.Exists(destination))
            {
                var info = new FileInfo(destination);
                var choice = mode switch
                {
                    OverwriteMode.OverwriteAll => ConflictChoice.Overwrite,
                    OverwriteMode.SkipAll => ConflictChoice.Skip,
                    OverwriteMode.RenameAll => ConflictChoice.Rename,
                    OverwriteMode.OverwriteOlder => info.LastWriteTime < (entry.LastModifiedTime ?? DateTime.MaxValue) ? ConflictChoice.Overwrite : ConflictChoice.Skip,
                    _ => await callbacks.ResolveConflictAsync(new ConflictInfo(
                        archivePath + "\\" + key, destination, entry.Size, entry.LastModifiedTime ?? DateTime.MinValue, info.Length, info.LastWriteTime)).ConfigureAwait(false),
                };
                switch (choice)
                {
                    case ConflictChoice.Cancel: return new TransferResult(done, skipped, errors, true);
                    case ConflictChoice.OverwriteAll: mode = OverwriteMode.OverwriteAll; break;
                    case ConflictChoice.SkipAll: mode = OverwriteMode.SkipAll; skipped++; bytesDone += entry.Size; continue;
                    case ConflictChoice.RenameAll: mode = OverwriteMode.RenameAll; destination = Unique(destination); break;
                    case ConflictChoice.OverwriteOlderAll: mode = OverwriteMode.OverwriteOlder; break;
                    case ConflictChoice.Skip: skipped++; bytesDone += entry.Size; continue;
                    case ConflictChoice.Rename: destination = Unique(destination); break;
                }
                if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var completed = false;
            try
            {
                await using (var input = openEntry())
                await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
                {
                    long fileDone = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        fileDone += read;
                        callbacks.Report(new TransferProgress(key, fileDone, entry.Size, bytesDone + fileDone, bytesTotal, done, filesTotal));
                    }
                }
                if (entry.LastModifiedTime is { } stamp) File.SetLastWriteTime(destination, stamp);
                completed = true;
                done++;
                bytesDone += entry.Size;
            }
            catch (OperationCanceledException)
            {
                return new TransferResult(done, skipped, errors, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
            {
                errors++;
                var answer = await callbacks.HandleErrorAsync(key, ex).ConfigureAwait(false);
                if (answer == ErrorChoice.Cancel) return new TransferResult(done, skipped, errors, true);
            }
            finally
            {
                if (!completed && File.Exists(destination))
                {
                    try { File.Delete(destination); } catch (IOException) { }
                }
            }
        }
        return new TransferResult(done, skipped, errors, false);
    }

    /// <summary>Creates a new archive from local files and folders (paths inside are relative to each source's parent).</summary>
    public static async Task<TransferResult> PackAsync(
        IReadOnlyList<string> sources, string archivePath, PackFormat format, ITransferCallbacks callbacks,
        CompressionLevel level = CompressionLevel.Optimal, PauseController? pause = null, CancellationToken ct = default)
    {
        var files = new List<(string Path, string EntryName, long Size)>();
        var emptyFolders = new List<string>();
        foreach (var source in sources)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(source).TrimEnd('\\'))!;
            if (Directory.Exists(source))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                var any = false;
                foreach (var f in new DirectoryInfo(source).EnumerateFiles("*", options))
                {
                    files.Add((f.FullName, Path.GetRelativePath(parent, f.FullName), f.Length));
                    any = true;
                }
                foreach (var d in new DirectoryInfo(source).EnumerateDirectories("*", options))
                    if (!d.EnumerateFileSystemInfos().Any()) emptyFolders.Add(Path.GetRelativePath(parent, d.FullName));
                if (!any) emptyFolders.Add(Path.GetRelativePath(parent, source));
            }
            else if (File.Exists(source))
            {
                files.Add((source, Path.GetFileName(source), new FileInfo(source).Length));
            }
        }

        var fullArchive = Path.GetFullPath(archivePath);
        files.RemoveAll(f => string.Equals(Path.GetFullPath(f.Path), fullArchive, StringComparison.OrdinalIgnoreCase)); // never pack into itself
        var bytesTotal = files.Sum(f => f.Size);
        long bytesDone = 0;
        var done = 0;
        var errors = 0;
        var buffer = new byte[BufferSize];
        var temp = archivePath + ".cc-partial";
        var completed = false;

        try
        {
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                if (format == PackFormat.Zip)
                {
                    using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                    foreach (var folder in emptyFolders) zip.CreateEntry(ToArchiveName(folder) + "/");
                    foreach (var (path, name, size) in files)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
                        try
                        {
                            var entry = zip.CreateEntry(ToArchiveName(name), level);
                            entry.LastWriteTime = File.GetLastWriteTime(path);
                            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
                            await using var target = entry.Open();
                            long fileDone = 0;
                            int read;
                            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                            {
                                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                                fileDone += read;
                                callbacks.Report(new TransferProgress(path, fileDone, size, bytesDone + fileDone, bytesTotal, done, files.Count));
                            }
                            done++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            errors++;
                            if (await callbacks.HandleErrorAsync(path, ex).ConfigureAwait(false) == ErrorChoice.Cancel)
                                return new TransferResult(done, 0, errors, true);
                        }
                        bytesDone += size;
                    }
                }
                else
                {
                    Stream tarStream = format == PackFormat.TarGz ? new GZipStream(output, level, leaveOpen: true) : output;
                    try
                    {
                        await using var tar = new TarWriter(tarStream, TarEntryFormat.Pax, leaveOpen: true);
                        foreach (var folder in emptyFolders)
                            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, ToArchiveName(folder) + "/"), ct).ConfigureAwait(false);
                        foreach (var (path, name, size) in files)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (pause is not null) await pause.WaitIfPausedAsync(ct).ConfigureAwait(false);
                            try
                            {
                                await tar.WriteEntryAsync(path, ToArchiveName(name), ct).ConfigureAwait(false);
                                done++;
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                errors++;
                                if (await callbacks.HandleErrorAsync(path, ex).ConfigureAwait(false) == ErrorChoice.Cancel)
                                    return new TransferResult(done, 0, errors, true);
                            }
                            bytesDone += size;
                            callbacks.Report(new TransferProgress(path, size, size, bytesDone, bytesTotal, done, files.Count));
                        }
                    }
                    finally
                    {
                        if (tarStream != output) await tarStream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            File.Move(temp, archivePath, overwrite: true);
            completed = true;
            return new TransferResult(done, 0, errors, false);
        }
        catch (OperationCanceledException)
        {
            return new TransferResult(done, 0, errors, true);
        }
        finally
        {
            if (!completed && File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string DefaultExtension(PackFormat format) => format switch
    {
        PackFormat.Tar => ".tar",
        PackFormat.TarGz => ".tar.gz",
        _ => ".zip",
    };

    private static string ToArchiveName(string relative) => relative.Replace('\\', '/');

    private static string Unique(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        return Path.Combine(dir, PathHelper.UniqueName(dir, Path.GetFileName(path)));
    }
}

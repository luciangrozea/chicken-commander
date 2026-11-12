using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;

namespace ChickenCommander.Core.Tools;

public enum SyncState { Equal, LeftNewer, RightNewer, LeftOnly, RightOnly, Different }

public enum SyncAction { None, CopyToRight, CopyToLeft }

public sealed record SyncOptions
{
    public bool Recursive { get; init; } = true;
    public bool CompareContent { get; init; }
    public bool IgnoreDate { get; init; }
    public string Mask { get; init; } = "*.*";

    /// <summary>FAT and network shares store times with 2-second precision.</summary>
    public TimeSpan DateTolerance { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>One file (or a folder that exists on one side only) and what to do with it.</summary>
public sealed class SyncItem(string relativePath, bool isDirectory, FileSystemInfo? left, FileSystemInfo? right, SyncState state)
{
    public string RelativePath { get; } = relativePath;
    public bool IsDirectory { get; } = isDirectory;
    public FileSystemInfo? Left { get; } = left;
    public FileSystemInfo? Right { get; } = right;
    public SyncState State { get; } = state;

    /// <summary>Default: copy the newer / only copy across; leave equal and ambiguous ones alone.</summary>
    public SyncAction Action { get; set; } = state switch
    {
        SyncState.LeftNewer or SyncState.LeftOnly => SyncAction.CopyToRight,
        SyncState.RightNewer or SyncState.RightOnly => SyncAction.CopyToLeft,
        _ => SyncAction.None,
    };
}

public static class DirectorySync
{
    public static IReadOnlyList<SyncItem> Compare(string leftRoot, string rightRoot, SyncOptions options, CancellationToken ct = default)
    {
        var items = new List<SyncItem>();
        CompareFolder(leftRoot, rightRoot, string.Empty, options, new WildcardMask(options.Mask), items, ct);
        return items;
    }

    private static void CompareFolder(string leftRoot, string rightRoot, string relative, SyncOptions o, WildcardMask mask, List<SyncItem> items, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var left = List(Path.Combine(leftRoot, relative));
        var right = List(Path.Combine(rightRoot, relative));
        var names = left.Keys.Union(right.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            var rel = Path.Combine(relative, name);
            left.TryGetValue(name, out var l);
            right.TryGetValue(name, out var r);
            var lDir = l is DirectoryInfo;
            var rDir = r is DirectoryInfo;

            if (lDir || rDir)
            {
                if (!o.Recursive) continue;
                if (lDir && rDir) CompareFolder(leftRoot, rightRoot, rel, o, mask, items, ct);
                else if (l is null) items.Add(new SyncItem(rel, true, null, r, SyncState.RightOnly));
                else if (r is null) items.Add(new SyncItem(rel, true, l, null, SyncState.LeftOnly));
                // A file on one side and a folder on the other: not something to sync automatically.
                else items.Add(new SyncItem(rel, false, l, r, SyncState.Different) { Action = SyncAction.None });
                continue;
            }

            if (!mask.IsMatch(name)) continue;
            if (l is null) { items.Add(new SyncItem(rel, false, null, r, SyncState.RightOnly)); continue; }
            if (r is null) { items.Add(new SyncItem(rel, false, l, null, SyncState.LeftOnly)); continue; }

            items.Add(new SyncItem(rel, false, l, r, CompareFiles((FileInfo)l, (FileInfo)r, o)));
        }
    }

    private static SyncState CompareFiles(FileInfo l, FileInfo r, SyncOptions o)
    {
        var sameSize = l.Length == r.Length;
        var diff = l.LastWriteTimeUtc - r.LastWriteTimeUtc;
        var sameTime = o.IgnoreDate || diff.Duration() <= o.DateTolerance;

        if (sameSize && sameTime)
        {
            if (!o.CompareContent) return SyncState.Equal;
            if (TextDiff.FilesEqual(l.FullName, r.FullName)) return SyncState.Equal;
            return o.IgnoreDate || diff.Duration() <= o.DateTolerance ? SyncState.Different : Newer(diff);
        }
        if (sameTime) return SyncState.Different; // same date, different size: needs a human decision
        if (o.CompareContent && sameSize && TextDiff.FilesEqual(l.FullName, r.FullName)) return SyncState.Equal;
        return Newer(diff);
    }

    private static SyncState Newer(TimeSpan leftMinusRight) => leftMinusRight > TimeSpan.Zero ? SyncState.LeftNewer : SyncState.RightNewer;

    private static Dictionary<string, FileSystemInfo> List(string path)
    {
        var result = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(path)) return result;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
            result[info.Name] = info;
        return result;
    }

    /// <summary>Copies every item that has an action, overwriting the older copy. Returns the number of files copied.</summary>
    public static async Task<int> ExecuteAsync(string leftRoot, string rightRoot, IEnumerable<SyncItem> items,
        ITransferCallbacks callbacks, CancellationToken ct = default)
    {
        var copied = 0;
        foreach (var item in items.Where(i => i.Action != SyncAction.None))
        {
            ct.ThrowIfCancellationRequested();
            var (from, to) = item.Action == SyncAction.CopyToRight
                ? (Path.Combine(leftRoot, item.RelativePath), Path.Combine(rightRoot, item.RelativePath))
                : (Path.Combine(rightRoot, item.RelativePath), Path.Combine(leftRoot, item.RelativePath));
            var result = await FileTransfer.RunAsync(
                new TransferRequest(TransferKind.Copy, [from], to, OverwriteMode.OverwriteAll), callbacks, ct: ct).ConfigureAwait(false);
            copied += result.FilesDone;
            if (result.Cancelled) break;
        }
        return copied;
    }
}

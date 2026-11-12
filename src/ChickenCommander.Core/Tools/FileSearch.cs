using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Viewer;

namespace ChickenCommander.Core.Tools;

public sealed record SearchCriteria
{
    public required string StartDirectory { get; init; }
    public string NameMask { get; init; } = "*.*";
    public bool Recursive { get; init; } = true;
    public string? ContainingText { get; init; }
    public bool MatchCase { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTime? ModifiedAfter { get; init; }
    public DateTime? ModifiedBefore { get; init; }
    public bool IncludeDirectories { get; init; } = true;
    public bool IncludeHidden { get; init; }
}

/// <summary>A match. For text searches, the first matching line (1-based) and its text.</summary>
public sealed record SearchHit(FileEntry Entry, int? LineNumber = null, string? Line = null);

public static class FileSearch
{
    private const long MaxTextSearchSize = 512L * 1024 * 1024;

    /// <summary>
    /// Walks the tree and yields matches as they are found. <paramref name="onDirectory"/> is called
    /// for every folder entered (for a "Searching in…" status line). Unreadable folders are skipped.
    /// </summary>
    public static IEnumerable<SearchHit> Search(SearchCriteria c, Action<string>? onDirectory = null, CancellationToken ct = default)
    {
        var mask = new WildcardMask(c.NameMask);
        var hasText = !string.IsNullOrEmpty(c.ContainingText);
        var skip = c.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = skip, RecurseSubdirectories = false };

        var pending = new Stack<string>();
        pending.Push(c.StartDirectory);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            onDirectory?.Invoke(dir);

            List<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Push subfolders in reverse so they are visited in alphabetical order.
            if (c.Recursive)
            {
                foreach (var sub in children.OfType<DirectoryInfo>()
                             .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint)) // avoid junction loops
                             .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
                    pending.Push(sub.FullName);
            }

            foreach (var info in children.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var isDir = info is DirectoryInfo;
                if (isDir && (!c.IncludeDirectories || hasText || c.MinSize is not null || c.MaxSize is not null)) continue;
                if (!mask.IsMatch(info.Name)) continue;

                var size = isDir ? 0 : ((FileInfo)info).Length;
                if (c.MinSize is { } min && size < min) continue;
                if (c.MaxSize is { } max && size > max) continue;
                if (c.ModifiedAfter is { } after && info.LastWriteTime < after) continue;
                if (c.ModifiedBefore is { } before && info.LastWriteTime > before) continue;

                var entry = new FileEntry(info.Name, info.FullName, isDir, size, info.LastWriteTime, info.Attributes);
                if (!hasText)
                {
                    yield return new SearchHit(entry);
                    continue;
                }

                if (size > MaxTextSearchSize) continue;
                var match = FindText(info.FullName, c.ContainingText!, c.MatchCase, ct);
                if (match is { } m) yield return new SearchHit(entry, m.Line, m.Text);
            }
        }
    }

    /// <summary>Finds the first line containing <paramref name="text"/>. Binary files are skipped.</summary>
    public static (int Line, string Text)? FindText(string path, string text, bool matchCase, CancellationToken ct = default)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            var head = new byte[Math.Min(8192, stream.Length)];
            stream.ReadExactly(head);
            if (TextDetector.IsBinary(head)) return null;
            stream.Position = 0;

            using var reader = new StreamReader(stream, TextDetector.DetectEncoding(head), detectEncodingFromByteOrderMarks: true);
            var number = 0;
            while (reader.ReadLine() is { } line)
            {
                number++;
                if ((number & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
                if (line.Contains(text, comparison))
                    return (number, line.Length > 300 ? line[..300] : line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}

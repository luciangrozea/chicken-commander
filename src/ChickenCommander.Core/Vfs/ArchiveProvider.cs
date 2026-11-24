using System.Collections.Concurrent;
using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.Core.Vfs;

/// <summary>Browses archives like folders: <c>C:\data\backup.zip\docs\2025</c>. Read-only.</summary>
public sealed class ArchiveProvider : IFileSystemProvider
{
    /// <summary>Extensions opened as archives when entered (Enter / Ctrl+PgDn).</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "jar", "apk", "nupkg", "epub", "7z", "rar", "tar", "tgz", "gz", "tbz", "tbz2", "bz2", "txz", "xz",
    };

    private readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, ArchiveIndex Index)> cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsArchiveFile(string path) =>
        Extensions.Contains(Path.GetExtension(path).TrimStart('.')) && File.Exists(path);

    /// <summary>
    /// Splits <c>C:\a\b.zip\c\d</c> into the archive file and the path inside it.
    /// Returns false when no part of the path is an archive file.
    /// </summary>
    public static bool TrySplit(string path, out string archivePath, out string inner)
    {
        archivePath = inner = string.Empty;
        if (path.Contains("://", StringComparison.Ordinal)) return false;
        var p = path.TrimEnd('\\');
        var start = 0;
        while (true)
        {
            var sep = p.IndexOf('\\', start);
            var candidate = sep < 0 ? p : p[..sep];
            if (candidate.Length > 3 && Extensions.Contains(Path.GetExtension(candidate).TrimStart('.')) && File.Exists(candidate))
            {
                archivePath = candidate;
                inner = sep < 0 ? string.Empty : p[(sep + 1)..];
                return true;
            }
            if (sep < 0) return false;
            start = sep + 1;
        }
    }

    public bool CanHandle(string path) => TrySplit(path, out _, out _);

    /// <summary>True when the archive's listing is already in memory and up to date, so browsing it is instant.</summary>
    public bool IsCached(string archivePath)
    {
        if (!cache.TryGetValue(archivePath, out var cached)) return false;
        try
        {
            var info = new FileInfo(archivePath);
            return cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public ArchiveIndex GetIndex(string archivePath, CancellationToken ct = default)
    {
        var info = new FileInfo(archivePath);
        if (cache.TryGetValue(archivePath, out var cached) && cached.Stamp == info.LastWriteTimeUtc && cached.Length == info.Length)
            return cached.Index;
        var index = ArchiveIndex.Load(archivePath, ct);
        cache[archivePath] = (info.LastWriteTimeUtc, info.Length, index);
        return index;
    }

    public IReadOnlyList<FileEntry> List(string path, CancellationToken ct = default)
    {
        if (!TrySplit(path, out var archive, out var inner)) throw new DirectoryNotFoundException(path);
        var index = GetIndex(archive, ct);
        return index.List(inner)?.ToList() ?? throw new DirectoryNotFoundException(path);
    }

    public bool DirectoryExists(string path)
    {
        if (!TrySplit(path, out var archive, out var inner)) return false;
        try
        {
            return GetIndex(archive).FolderExists(inner);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The parent of an archive's root is the folder that contains the archive file.</summary>
    public string? GetParent(string path)
    {
        var p = path.TrimEnd('\\');
        var i = p.LastIndexOf('\\');
        return i < 0 ? null : (i == 2 && p[1] == ':' ? p[..3] : p[..i]);
    }

    public string Normalize(string path)
    {
        if (!TrySplit(path, out var archive, out var inner)) return path;
        archive = Path.GetFullPath(archive);
        return inner.Length == 0 ? archive : archive + "\\" + inner;
    }

    public string Combine(string directory, string name) => directory.TrimEnd('\\') + "\\" + name;

    public bool IsReadOnly => true;
}

using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.Core.Vfs;

/// <summary>
/// The directory tree of one archive, read once and cached. Folders that only exist implicitly
/// (an entry "a/b/c.txt" without "a/" entries) are created so every level can be browsed.
/// </summary>
public sealed class ArchiveIndex
{
    private readonly Dictionary<string, Dictionary<string, FileEntry>> folders = new(StringComparer.OrdinalIgnoreCase);

    private ArchiveIndex(string archivePath)
    {
        ArchivePath = archivePath;
        folders[string.Empty] = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
    }

    public string ArchivePath { get; }
    public bool IsEncrypted { get; private set; }
    public long TotalSize { get; private set; }

    public static ArchiveIndex Load(string archivePath, CancellationToken ct = default)
    {
        var index = new ArchiveIndex(archivePath);
        foreach (var (entry, _) in ArchiveReading.Entries(archivePath))
        {
            ct.ThrowIfCancellationRequested();
            var inner = NormalizeKey(entry.Key);
            if (inner.Length == 0) continue;
            index.IsEncrypted |= entry.IsEncrypted;
            var modified = entry.LastModifiedTime ?? File.GetLastWriteTime(archivePath);
            if (entry.IsDirectory) index.AddFolder(inner, modified);
            else index.AddFile(inner, entry.Size, modified);
        }
        return index;
    }

    /// <summary>Archive keys use '/', sometimes '\', sometimes a leading "./".</summary>
    public static string NormalizeKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        var k = key.Replace('/', '\\').Trim('\\');
        while (k.StartsWith(".\\", StringComparison.Ordinal)) k = k[2..];
        return k == "." ? string.Empty : k;
    }

    public bool FolderExists(string inner) => folders.ContainsKey(inner);

    public IReadOnlyCollection<FileEntry>? List(string inner) =>
        folders.TryGetValue(inner, out var children) ? children.Values : null;

    /// <summary>Files at or below <paramref name="inner"/> (a file or a folder), with their sizes.</summary>
    public IEnumerable<FileEntry> FilesUnder(string inner)
    {
        if (!folders.ContainsKey(inner))
        {
            var parent = Parent(inner);
            if (folders.TryGetValue(parent, out var siblings) && siblings.TryGetValue(Name(inner), out var file) && !file.IsDirectory)
                yield return file;
            yield break;
        }
        var stack = new Stack<string>([inner]);
        while (stack.Count > 0)
        {
            foreach (var child in folders[stack.Pop()].Values)
            {
                if (child.IsDirectory) stack.Push(InnerOf(child.FullPath));
                else yield return child;
            }
        }
    }

    private void AddFile(string inner, long size, DateTime modified)
    {
        var parent = Parent(inner);
        EnsureFolder(parent, modified);
        folders[parent][Name(inner)] = Entry(inner, false, size, modified);
        TotalSize += size;
    }

    private void AddFolder(string inner, DateTime modified)
    {
        EnsureFolder(inner, modified);
        // An explicit folder entry carries the real date; replace the placeholder made by EnsureFolder.
        var parent = Parent(inner);
        folders[parent][Name(inner)] = Entry(inner, true, 0, modified);
    }

    private void EnsureFolder(string inner, DateTime modified)
    {
        if (folders.ContainsKey(inner)) return;
        folders[inner] = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        var parent = Parent(inner);
        EnsureFolder(parent, modified);
        folders[parent].TryAdd(Name(inner), Entry(inner, true, 0, modified));
    }

    private FileEntry Entry(string inner, bool isDir, long size, DateTime modified) =>
        new(Name(inner), ArchivePath + "\\" + inner, isDir, size, modified, isDir ? FileAttributes.Directory : FileAttributes.Normal);

    private string InnerOf(string fullPath) => fullPath[(ArchivePath.Length + 1)..];

    private static string Parent(string inner)
    {
        var i = inner.LastIndexOf('\\');
        return i < 0 ? string.Empty : inner[..i];
    }

    private static string Name(string inner) => inner[(inner.LastIndexOf('\\') + 1)..];
}

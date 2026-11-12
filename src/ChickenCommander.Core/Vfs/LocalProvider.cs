using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.Core.Vfs;

public sealed class LocalProvider : IFileSystemProvider
{
    public bool ShowHidden { get; set; }

    public bool CanHandle(string path) => true; // fallback provider

    public IReadOnlyList<FileEntry> List(string path, CancellationToken ct = default) =>
        DirectoryReader.Read(path, SortOrder.Default, ShowHidden).Where(e => !e.IsParentLink).ToList();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? GetParent(string path) => Directory.GetParent(Normalize(path))?.FullName;

    public string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    public string Combine(string directory, string name) => Path.Combine(directory, name);

    public bool IsReadOnly => false;
}

using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Remote;

namespace ChickenCommander.Core.Vfs;

/// <summary>Routes a panel path to the right provider: remote server, archive, or local disk.</summary>
public sealed class VirtualFileSystem
{
    public VirtualFileSystem()
    {
        Sessions = new RemoteSessions();
        Remote = new RemoteProvider(Sessions);
    }

    public LocalProvider Local { get; } = new();
    public ArchiveProvider Archives { get; } = new();
    public RemoteProvider Remote { get; }
    public RemoteSessions Sessions { get; }

    public IFileSystemProvider For(string path) =>
        Remote.CanHandle(path) ? Remote : Archives.CanHandle(path) ? Archives : Local;

    public LocationKind KindOf(string path) =>
        Remote.CanHandle(path) ? LocationKind.Remote : Archives.CanHandle(path) ? LocationKind.Archive : LocationKind.Local;

    /// <summary>Sorted listing with a ".." entry when there is a parent (the parent of an archive root is its folder).</summary>
    public IReadOnlyList<FileEntry> Read(string path, SortOrder sort, bool showHidden, CancellationToken ct = default)
    {
        var provider = For(path);
        if (provider is LocalProvider) return DirectoryReader.Read(path, sort, showHidden);

        var entries = provider.List(path, ct).Where(e => showHidden || !e.IsHidden).ToList();
        entries.Sort(new FileEntryComparer(sort));
        if (provider.GetParent(path) is { } parent) entries.Insert(0, FileEntry.ParentLink(parent));
        return entries;
    }

    public bool DirectoryExists(string path) => For(path).DirectoryExists(path);

    public string? GetParent(string path) => For(path).GetParent(path);

    public string Normalize(string path) => For(path).Normalize(path);
}

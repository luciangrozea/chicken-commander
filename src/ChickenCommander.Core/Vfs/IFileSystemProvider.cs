using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.Core.Vfs;

/// <summary>
/// A place a panel can show: the local disk, the inside of an archive, or a remote server.
/// Paths stay strings so panels, history and tabs work the same everywhere:
/// <c>C:\dir</c>, <c>C:\dir\photos.zip\2025</c>, <c>sftp://myserver/home/me</c>.
/// </summary>
public interface IFileSystemProvider
{
    bool CanHandle(string path);

    /// <summary>Children of a directory (no ".." entry).</summary>
    IReadOnlyList<FileEntry> List(string path, CancellationToken ct = default);

    bool DirectoryExists(string path);

    /// <summary>Parent directory, or null at the top of this provider.</summary>
    string? GetParent(string path);

    /// <summary>Canonical spelling of a path (full path, no trailing separator except at roots).</summary>
    string Normalize(string path);

    /// <summary>Joins a directory and a child name using this provider's separator.</summary>
    string Combine(string directory, string name);

    /// <summary>True when files can only be read here (archives).</summary>
    bool IsReadOnly { get; }
}

/// <summary>Where a path lives; decides which copy/delete engine applies.</summary>
public enum LocationKind { Local, Archive, Remote }

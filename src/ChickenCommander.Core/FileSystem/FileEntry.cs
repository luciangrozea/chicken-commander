namespace ChickenCommander.Core.FileSystem;

/// <summary>One row in a file panel: a file, a directory, or the ".." parent link.</summary>
public sealed record FileEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    FileAttributes Attributes,
    bool IsParentLink = false)
{
    /// <summary>Name without extension (directories keep their full name).</summary>
    public string BaseName =>
        IsDirectory || IsParentLink || string.IsNullOrEmpty(Extension)
            ? Name
            : Name[..^(Extension.Length + 1)];

    /// <summary>Extension without the leading dot; empty for directories and dot-files.</summary>
    public string Extension
    {
        get
        {
            if (IsDirectory || IsParentLink) return string.Empty;
            var dot = Name.LastIndexOf('.');
            return dot <= 0 || dot == Name.Length - 1 ? string.Empty : Name[(dot + 1)..];
        }
    }

    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);

    /// <summary>Compact attribute string in the classic "rahs" style.</summary>
    public string AttributeString => AttributeFormatter.Format(Attributes);

    public static FileEntry ParentLink(string parentPath) =>
        new("..", parentPath, IsDirectory: true, Size: 0, DateTime.MinValue, FileAttributes.Directory, IsParentLink: true);
}

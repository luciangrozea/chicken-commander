namespace ChickenCommander.Core.FileSystem;

public static class DirectoryReader
{
    /// <summary>
    /// Lists a directory, sorted for display, with a ".." entry when the directory has a parent.
    /// Entries that cannot be read (permissions, broken links) are skipped rather than failing the whole listing.
    /// </summary>
    public static IReadOnlyList<FileEntry> Read(string path, SortOrder order, bool showHidden = false)
    {
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException(path);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        };

        var entries = new List<FileEntry>();
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
        {
            try
            {
                var isDir = info is DirectoryInfo;
                entries.Add(new FileEntry(
                    info.Name,
                    info.FullName,
                    isDir,
                    isDir ? 0 : ((FileInfo)info).Length,
                    info.LastWriteTime,
                    info.Attributes));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        entries.Sort(new FileEntryComparer(order));

        if (dir.Parent is { } parent)
            entries.Insert(0, FileEntry.ParentLink(parent.FullName));

        return entries;
    }
}

namespace ChickenCommander.Core.Operations;

/// <summary>A change to apply: flags to set, flags to clear, optional new timestamps.</summary>
public sealed record AttributeChange(
    FileAttributes Set = 0,
    FileAttributes Clear = 0,
    DateTime? Modified = null,
    DateTime? Created = null,
    bool IncludeSubfolders = false)
{
    /// <summary>Only these flags can be changed by the user.</summary>
    public const FileAttributes Editable = FileAttributes.ReadOnly | FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.System;
}

public static class AttributeChanger
{
    /// <summary>Applies the change to every path (and their contents when requested). Returns failures.</summary>
    public static IReadOnlyList<(string Path, string Error)> Apply(IEnumerable<string> paths, AttributeChange change)
    {
        var errors = new List<(string, string)>();
        foreach (var path in paths)
        {
            ApplyOne(path, change, errors);
            if (change.IncludeSubfolders && Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
                foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", options))
                    ApplyOne(child, change, errors);
            }
        }
        return errors;
    }

    /// <summary>
    /// Combined state of many items for a three-state checkbox: true = all have it,
    /// false = none, null = mixed.
    /// </summary>
    public static bool? Combined(IEnumerable<FileAttributes> items, FileAttributes flag)
    {
        bool any = false, all = true;
        foreach (var a in items)
        {
            if (a.HasFlag(flag)) any = true;
            else all = false;
        }
        return all && any ? true : any ? null : false;
    }

    private static void ApplyOne(string path, AttributeChange change, List<(string, string)> errors)
    {
        try
        {
            var isDir = Directory.Exists(path);
            var current = File.GetAttributes(path);
            var updated = (current | (change.Set & AttributeChange.Editable)) & ~(change.Clear & AttributeChange.Editable);

            // Timestamps first: a read-only file refuses timestamp changes on some systems.
            if (current.HasFlag(FileAttributes.ReadOnly) && (change.Modified is not null || change.Created is not null))
                File.SetAttributes(path, current & ~FileAttributes.ReadOnly);

            if (change.Modified is { } m)
            {
                if (isDir) Directory.SetLastWriteTime(path, m);
                else File.SetLastWriteTime(path, m);
            }
            if (change.Created is { } c)
            {
                if (isDir) Directory.SetCreationTime(path, c);
                else File.SetCreationTime(path, c);
            }

            File.SetAttributes(path, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            errors.Add((path, ex.Message));
        }
    }
}

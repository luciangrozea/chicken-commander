namespace ChickenCommander.Core.FileSystem;

public static class PathHelper
{
    /// <summary>
    /// Resolves what the user typed in a path box: absolute paths, paths relative to the current
    /// directory, "..", and bare drive letters ("d:" → "D:\").
    /// </summary>
    public static string Resolve(string currentDirectory, string input)
    {
        var text = input.Trim().Trim('"');
        if (text.Length == 0) return currentDirectory;

        if (text.Length == 2 && char.IsLetter(text[0]) && text[1] == ':')
            return char.ToUpperInvariant(text[0]) + @":\";

        var combined = Path.IsPathRooted(text) ? text : Path.Combine(currentDirectory, text);
        var full = Path.GetFullPath(combined);
        return full;
    }

    /// <summary>Returns a name like "New folder (2)" that does not exist yet in <paramref name="directory"/>.</summary>
    public static string UniqueName(string directory, string desired)
    {
        if (!Exists(Path.Combine(directory, desired))) return desired;
        var ext = Path.GetExtension(desired);
        var stem = Path.GetFileNameWithoutExtension(desired);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!Exists(Path.Combine(directory, candidate))) return candidate;
        }
    }

    /// <summary>
    /// Splits a panel path into clickable steps, each with the full path to go to:
    /// "D:\a\b" → D: (D:\), a (D:\a), b (D:\a\b). Works for archives ("D:\x.zip\dir"),
    /// network shares ("\\server\share\dir") and servers ("sftp://name/dir").
    /// </summary>
    public static IReadOnlyList<PathSegment> Segments(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        var result = new List<PathSegment>();
        string root, rest;
        char separator;
        var scheme = path.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0)
        {
            separator = '/';
            var rootEnd = path.IndexOf('/', scheme + 3);
            root = rootEnd < 0 ? path + "/" : path[..(rootEnd + 1)];
            rest = rootEnd < 0 ? string.Empty : path[(rootEnd + 1)..];
        }
        else
        {
            separator = '\\';
            root = Path.GetPathRoot(path) is { Length: > 0 } r ? r : path;
            rest = path[root.Length..];
        }
        result.Add(new PathSegment(root.TrimEnd('\\', '/'), root));

        var current = root;
        foreach (var part in rest.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.EndsWith(separator) ? current + part : current + separator + part;
            result.Add(new PathSegment(part, current));
        }
        return result;
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
}

/// <summary>One step of a path bar: what is shown and where a click goes.</summary>
public sealed record PathSegment(string Label, string Path);

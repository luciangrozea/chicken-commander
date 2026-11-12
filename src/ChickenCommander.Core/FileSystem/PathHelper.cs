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

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
}

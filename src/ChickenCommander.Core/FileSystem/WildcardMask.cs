using System.Text.RegularExpressions;

namespace ChickenCommander.Core.FileSystem;

/// <summary>
/// File masks such as "*.txt;*.md" or "report??.doc". A mask with a leading '|' excludes:
/// "*.* | *.bak" matches everything except .bak files.
/// </summary>
public sealed class WildcardMask
{
    private readonly Regex[] include;
    private readonly Regex[] exclude;

    public WildcardMask(string mask)
    {
        var parts = mask.Split('|', 2);
        include = Compile(parts[0]);
        exclude = parts.Length > 1 ? Compile(parts[1]) : [];
        if (include.Length == 0) include = [ToRegex("*")];
    }

    public bool IsMatch(string fileName) =>
        include.Any(r => r.IsMatch(fileName)) && !exclude.Any(r => r.IsMatch(fileName));

    private static Regex[] Compile(string list) =>
        list.Split([';', ' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToRegex)
            .ToArray();

    private static Regex ToRegex(string pattern)
    {
        // "*.*" conventionally means "every file", including names without a dot.
        if (pattern == "*.*") pattern = "*";
        var body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + body + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

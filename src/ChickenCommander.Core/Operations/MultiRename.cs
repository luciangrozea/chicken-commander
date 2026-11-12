using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ChickenCommander.Core.Operations;

public enum CaseMode { Unchanged, Lower, Upper, FirstUpper, TitleCase }

/// <summary>
/// Settings of the multi-rename tool. Masks use placeholders:
/// [N] name, [N2-5] / [N3] / [N2-] parts of the name, [E] extension (same ranges), [C] counter,
/// [P] parent folder, [Y] [M] [D] [h] [m] [s] modification date/time, [YMD] = yyyyMMdd.
/// </summary>
public sealed record RenameOptions
{
    public string NameMask { get; init; } = "[N]";
    public string ExtensionMask { get; init; } = "[E]";
    public string Search { get; init; } = string.Empty;
    public string Replace { get; init; } = string.Empty;
    public bool UseRegex { get; init; }
    public bool MatchCase { get; init; }
    public CaseMode Case { get; init; } = CaseMode.Unchanged;
    public int CounterStart { get; init; } = 1;
    public int CounterStep { get; init; } = 1;
    public int CounterDigits { get; init; } = 1;
}

public sealed record RenameSource(string FullPath, bool IsDirectory, DateTime Modified);

public sealed record RenamePreview(RenameSource Source, string OldName, string NewName, string? Error)
{
    public bool Changes => Error is null && !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

public static class MultiRename
{
    private static readonly Regex Placeholder = new(@"\[(?<tag>YMD|[NECPYMDhms])(?<from>\d+)?(?<dash>-)?(?<to>\d+)?\]", RegexOptions.Compiled);
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>Computes the new name for one entry. <paramref name="index"/> is its 0-based position in the batch.</summary>
    public static string Apply(RenameOptions o, RenameSource source, int index)
    {
        var fileName = Path.GetFileName(source.FullPath);
        var (name, ext) = Split(fileName, source.IsDirectory);
        var parent = Path.GetFileName(Path.GetDirectoryName(source.FullPath) ?? string.Empty);
        var counter = (o.CounterStart + (long)index * o.CounterStep).ToString(CultureInfo.InvariantCulture)
            .PadLeft(Math.Max(1, o.CounterDigits), '0');

        string Expand(string mask) => Placeholder.Replace(mask, m =>
        {
            var tag = m.Groups["tag"].Value;
            var value = tag switch
            {
                "N" => name,
                "E" => ext,
                "C" => counter,
                "P" => parent,
                "Y" => source.Modified.ToString("yyyy", CultureInfo.InvariantCulture),
                "M" => source.Modified.ToString("MM", CultureInfo.InvariantCulture),
                "D" => source.Modified.ToString("dd", CultureInfo.InvariantCulture),
                "h" => source.Modified.ToString("HH", CultureInfo.InvariantCulture),
                "m" => source.Modified.ToString("mm", CultureInfo.InvariantCulture),
                "s" => source.Modified.ToString("ss", CultureInfo.InvariantCulture),
                "YMD" => source.Modified.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                _ => m.Value,
            };
            return tag is "N" or "E" ? Slice(value, m) : value;
        });

        var newName = Expand(o.NameMask);
        var newExt = Expand(o.ExtensionMask);
        var result = newExt.Length > 0 ? $"{newName}.{newExt}" : newName;

        if (o.Search.Length > 0)
        {
            if (o.UseRegex)
            {
                var regex = new Regex(o.Search, o.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                result = regex.Replace(result, o.Replace);
            }
            else
            {
                result = result.Replace(o.Search, o.Replace, o.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }
        }

        return ApplyCase(result, o.Case);
    }

    /// <summary>Previews a batch and flags invalid names and collisions (inside the batch or with other files).</summary>
    public static IReadOnlyList<RenamePreview> Preview(RenameOptions options, IReadOnlyList<RenameSource> sources)
    {
        var results = new List<RenamePreview>(sources.Count);
        string? regexError = null;
        if (options.UseRegex && options.Search.Length > 0)
        {
            try { _ = new Regex(options.Search); }
            catch (ArgumentException ex) { regexError = "Invalid regular expression: " + ex.Message; }
        }

        var newPaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourcePaths = sources.Select(s => s.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sources.Count; i++)
        {
            var s = sources[i];
            var oldName = Path.GetFileName(s.FullPath);
            if (regexError is not null)
            {
                results.Add(new RenamePreview(s, oldName, oldName, regexError));
                continue;
            }

            var newName = Apply(options, s, i);
            string? error = null;
            if (string.IsNullOrWhiteSpace(newName)) error = "Empty name";
            else if (newName.IndexOfAny(InvalidChars) >= 0) error = "Invalid character in name";
            else if (newName.EndsWith('.') || newName.EndsWith(' ')) error = "Name cannot end with a dot or space";

            var newPath = Path.Combine(Path.GetDirectoryName(s.FullPath)!, newName);
            if (error is null)
            {
                if (newPaths.TryGetValue(newPath, out _)) error = "Duplicate name";
                else if (!string.Equals(newPath, s.FullPath, StringComparison.OrdinalIgnoreCase) &&
                         !sourcePaths.Contains(newPath) && (File.Exists(newPath) || Directory.Exists(newPath)))
                    error = "A file with this name already exists";
            }
            newPaths.TryAdd(newPath, i);
            results.Add(new RenamePreview(s, oldName, newName, error));
        }
        return results;
    }

    /// <summary>
    /// Renames everything that changes, in two phases (via temporary names) so that swaps like
    /// a→b, b→a work. Returns the (newPath, oldPath) pairs needed to undo.
    /// </summary>
    public static IReadOnlyList<(string NewPath, string OldPath)> Execute(IReadOnlyList<RenamePreview> previews)
    {
        if (previews.Any(p => p.Error is not null)) throw new InvalidOperationException("Fix the errors before renaming.");
        var changes = previews.Where(p => p.Changes).ToList();
        var staged = new List<(string Temp, string Final, string Original, bool IsDir)>();

        try
        {
            foreach (var p in changes)
            {
                var dir = Path.GetDirectoryName(p.Source.FullPath)!;
                var temp = Path.Combine(dir, $"~cc-rename-{Guid.NewGuid():N}");
                Move(p.Source.FullPath, temp, p.Source.IsDirectory);
                staged.Add((temp, Path.Combine(dir, p.NewName), p.Source.FullPath, p.Source.IsDirectory));
            }
            var done = new List<(string, string)>();
            foreach (var (temp, final, original, isDir) in staged)
            {
                Move(temp, final, isDir);
                done.Add((final, original));
            }
            return done;
        }
        catch
        {
            // Put back whatever is still under a temporary name, so nothing gets lost.
            foreach (var (temp, _, original, isDir) in staged)
            {
                if (File.Exists(temp) || Directory.Exists(temp))
                {
                    try { Move(temp, original, isDir); } catch (IOException) { }
                }
            }
            throw;
        }
    }

    /// <summary>Reverts a batch returned by <see cref="Execute"/>.</summary>
    public static void Undo(IReadOnlyList<(string NewPath, string OldPath)> renamed)
    {
        var previews = renamed.Select(r =>
        {
            var isDir = Directory.Exists(r.NewPath);
            var src = new RenameSource(r.NewPath, isDir, DateTime.MinValue);
            return new RenamePreview(src, Path.GetFileName(r.NewPath), Path.GetFileName(r.OldPath), null);
        }).ToList();
        Execute(previews);
    }

    private static void Move(string from, string to, bool isDir)
    {
        if (isDir) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static (string Name, string Ext) Split(string fileName, bool isDirectory)
    {
        if (isDirectory) return (fileName, string.Empty);
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? (fileName, string.Empty) : (fileName[..dot], fileName[(dot + 1)..]);
    }

    /// <summary>[N3] = 3rd char, [N2-5] = chars 2..5, [N2-] = from 2nd char on (1-based, clamped).</summary>
    private static string Slice(string value, Match m)
    {
        if (!m.Groups["from"].Success) return value;
        var from = int.Parse(m.Groups["from"].Value, CultureInfo.InvariantCulture);
        int to;
        if (!m.Groups["dash"].Success) to = from;
        else if (m.Groups["to"].Success) to = int.Parse(m.Groups["to"].Value, CultureInfo.InvariantCulture);
        else to = value.Length;

        from = Math.Max(1, from);
        to = Math.Min(value.Length, to);
        return from > to ? string.Empty : value.Substring(from - 1, to - from + 1);
    }

    private static string ApplyCase(string s, CaseMode mode) => mode switch
    {
        CaseMode.Lower => s.ToLowerInvariant(),
        CaseMode.Upper => s.ToUpperInvariant(),
        CaseMode.FirstUpper => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant(),
        CaseMode.TitleCase => TitleCase(s),
        _ => s,
    };

    private static string TitleCase(string s)
    {
        var sb = new StringBuilder(s.Length);
        var startOfWord = true;
        foreach (var c in s)
        {
            sb.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            startOfWord = !char.IsLetterOrDigit(c) && c != '\'';
        }
        return sb.ToString();
    }
}

using System.Text;

namespace ChickenCommander.Core.Settings;

public enum ButtonKind { Command, Program, Separator }

/// <summary>One button of the button bar: a built-in command, or an external program with parameters.</summary>
public sealed record ButtonBarItem
{
    public ButtonKind Kind { get; init; } = ButtonKind.Command;

    /// <summary>For <see cref="ButtonKind.Command"/>: the command id (e.g. "Refresh").</summary>
    public string? CommandId { get; init; }

    /// <summary>For <see cref="ButtonKind.Program"/>: the program to start and its arguments (placeholders allowed).</summary>
    public string? Program { get; init; }
    public string? Arguments { get; init; }

    /// <summary>Tooltip; for programs also the label.</summary>
    public string? Label { get; init; }

    /// <summary>A Segoe Fluent Icons character; programs get a default one.</summary>
    public string? Glyph { get; init; }

    public static ButtonBarItem Separator => new() { Kind = ButtonKind.Separator };

    public static ButtonBarItem ForCommand(string id) => new() { Kind = ButtonKind.Command, CommandId = id };
}

/// <summary>Fills placeholders in program arguments with the panels' state.</summary>
public static class ButtonArguments
{
    /// <summary>
    /// %P current folder, %N name under the cursor, %F full path under the cursor,
    /// %S selected files (full paths, quoted, space separated), %T other panel's folder, %% a percent sign.
    /// Paths are quoted when they contain spaces, unless the placeholder is already inside quotes.
    /// </summary>
    public static string Expand(string template, string currentFolder, string? currentName, IReadOnlyList<string> selection, string targetFolder)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c == '"') inQuotes = !inQuotes;
            if (c != '%' || i + 1 >= template.Length)
            {
                sb.Append(c);
                continue;
            }
            var code = char.ToUpperInvariant(template[i + 1]);
            string? value = code switch
            {
                'P' => Quote(currentFolder, inQuotes),
                'N' => currentName is null ? string.Empty : Quote(currentName, inQuotes),
                'F' => currentName is null ? string.Empty : Quote(Path.Combine(currentFolder, currentName), inQuotes),
                'S' => string.Join(' ', selection.Select(s => Quote(s, inQuotes: false))),
                'T' => Quote(targetFolder, inQuotes),
                '%' => "%",
                _ => null,
            };
            if (value is null)
            {
                sb.Append(c);
                continue;
            }
            sb.Append(value);
            i++;
        }
        return sb.ToString();
    }

    private static string Quote(string value, bool inQuotes) =>
        inQuotes || !value.Contains(' ') ? value : $"\"{value}\"";
}

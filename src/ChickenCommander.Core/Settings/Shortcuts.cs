namespace ChickenCommander.Core.Settings;

/// <summary>Keyboard gestures as text: "Ctrl+Shift+F5", "Alt+Left", "F3". Modifier order is canonical.</summary>
public static class Gesture
{
    private static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];

    private static readonly Dictionary<string, string> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = "Ctrl", ["control"] = "Ctrl", ["alt"] = "Alt", ["shift"] = "Shift", ["win"] = "Win", ["windows"] = "Win",
    };

    /// <summary>
    /// Canonical spelling ("shift + ctrl + f5" → "Ctrl+Shift+F5"), or null when there is no key
    /// (empty, or modifiers only). Key names are kept, with the first letter upper-cased.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        // "Ctrl++" means Ctrl and the plus key.
        if (text.TrimEnd().EndsWith("++", StringComparison.Ordinal)) parts = [.. parts.Where(p => p.Length > 0), "+"];
        var modifiers = new HashSet<string>();
        string? key = null;
        foreach (var part in parts.Where(p => p.Length > 0))
        {
            if (ModifierAliases.TryGetValue(part, out var mod)) modifiers.Add(mod);
            else if (key is null) key = part.Length == 1 ? part.ToUpperInvariant() : char.ToUpperInvariant(part[0]) + part[1..];
            else return null; // two non-modifier keys
        }
        if (key is null) return null;
        return string.Join('+', ModifierOrder.Where(modifiers.Contains).Append(key));
    }
}

/// <summary>
/// Which command a key gesture runs: built-in defaults, overridden per command by the user.
/// An override with no gestures unbinds the command.
/// </summary>
public sealed class ShortcutMap
{
    private readonly Dictionary<string, List<string>> byCommand = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> byGesture = new(StringComparer.OrdinalIgnoreCase);

    public ShortcutMap(IReadOnlyDictionary<string, string[]> defaults, IReadOnlyDictionary<string, List<string>>? overrides = null)
    {
        foreach (var (command, gestures) in defaults)
        {
            var effective = overrides is not null && overrides.TryGetValue(command, out var custom) ? custom : gestures.ToList();
            byCommand[command] = effective.Select(Gesture.Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        // When two commands claim one gesture, the first one (in definition order) wins; Conflicts() reports it.
        foreach (var (command, gestures) in byCommand)
            foreach (var g in gestures)
                byGesture.TryAdd(g, command);
    }

    public string? CommandFor(string gesture) =>
        Gesture.Normalize(gesture) is { } g && byGesture.TryGetValue(g, out var command) ? command : null;

    public IReadOnlyList<string> GesturesFor(string command) =>
        byCommand.TryGetValue(command, out var gestures) ? gestures : [];

    /// <summary>Gestures assigned to more than one command.</summary>
    public IReadOnlyList<(string Gesture, IReadOnlyList<string> Commands)> Conflicts() =>
        byCommand.SelectMany(kv => kv.Value.Select(g => (Gesture: g, Command: kv.Key)))
            .GroupBy(x => x.Gesture, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(x => x.Command).ToList()))
            .ToList();

    /// <summary>Only the commands whose gestures differ from the defaults, for saving.</summary>
    public static Dictionary<string, List<string>> Diff(IReadOnlyDictionary<string, string[]> defaults, IReadOnlyDictionary<string, List<string>> current)
    {
        var result = new Dictionary<string, List<string>>();
        foreach (var (command, gestures) in current)
        {
            var normalized = gestures.Select(Gesture.Normalize).OfType<string>().ToList();
            var defaultList = defaults.TryGetValue(command, out var d) ? d.Select(Gesture.Normalize).OfType<string>().ToList() : [];
            if (!normalized.SequenceEqual(defaultList, StringComparer.OrdinalIgnoreCase)) result[command] = normalized;
        }
        return result;
    }
}

namespace ChickenCommander.Core.FileSystem;

/// <summary>Back/forward history of visited directories, like a web browser.</summary>
public sealed class NavigationHistory
{
    private const int MaxEntries = 50;
    private readonly List<string> back = [];
    private readonly List<string> forward = [];

    public string? Current { get; private set; }

    public bool CanGoBack => back.Count > 0;
    public bool CanGoForward => forward.Count > 0;

    /// <summary>Most recent first, current excluded.</summary>
    public IReadOnlyList<string> BackEntries => Enumerable.Reverse(back).ToList();
    public IReadOnlyList<string> ForwardEntries => Enumerable.Reverse(forward).ToList();

    /// <summary>Records a normal navigation: clears the forward list.</summary>
    public void Visit(string path)
    {
        if (string.Equals(Current, path, StringComparison.OrdinalIgnoreCase)) return;
        if (Current is not null)
        {
            back.Add(Current);
            if (back.Count > MaxEntries) back.RemoveAt(0);
        }
        forward.Clear();
        Current = path;
    }

    public string? GoBack()
    {
        if (back.Count == 0) return null;
        if (Current is not null) forward.Add(Current);
        Current = back[^1];
        back.RemoveAt(back.Count - 1);
        return Current;
    }

    public string? GoForward()
    {
        if (forward.Count == 0) return null;
        if (Current is not null) back.Add(Current);
        Current = forward[^1];
        forward.RemoveAt(forward.Count - 1);
        return Current;
    }

    /// <summary>Distinct recent directories (current first) for the Alt+Down history menu.</summary>
    public IReadOnlyList<string> Recent()
    {
        var all = new List<string>();
        if (Current is not null) all.Add(Current);
        all.AddRange(BackEntries);
        all.AddRange(ForwardEntries);
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

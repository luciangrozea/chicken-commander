namespace ChickenCommander.Core.FileSystem;

public enum SortColumn { Name, Extension, Size, Modified, Attributes }

public readonly record struct SortOrder(SortColumn Column, bool Descending = false)
{
    public static SortOrder Default => new(SortColumn.Name);

    /// <summary>Clicking the active column flips direction; clicking another column sorts it ascending.</summary>
    public SortOrder Toggle(SortColumn column) =>
        column == Column ? this with { Descending = !Descending } : new SortOrder(column);
}

/// <summary>
/// Orders entries the way dual-pane file managers do: ".." first, then directories, then files.
/// Directories are always sorted by name (except when sorting by date); the chosen column applies to files.
/// </summary>
public sealed class FileEntryComparer(SortOrder order) : IComparer<FileEntry>
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (x.IsParentLink != y.IsParentLink) return x.IsParentLink ? -1 : 1;
        if (x.IsDirectory != y.IsDirectory) return x.IsDirectory ? -1 : 1;

        int result;
        if (x.IsDirectory)
        {
            result = order.Column == SortColumn.Modified
                ? x.Modified.CompareTo(y.Modified)
                : NaturalCompare(x.Name, y.Name);
        }
        else
        {
            result = order.Column switch
            {
                SortColumn.Extension => Names.Compare(x.Extension, y.Extension),
                SortColumn.Size => x.Size.CompareTo(y.Size),
                SortColumn.Modified => x.Modified.CompareTo(y.Modified),
                SortColumn.Attributes => Names.Compare(x.AttributeString, y.AttributeString),
                _ => 0,
            };
            if (result == 0) result = NaturalCompare(x.Name, y.Name);
        }

        return order.Descending ? -result : result;
    }

    /// <summary>Compares names so that "file2" sorts before "file10".</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0');
                var nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var cmp = string.CompareOrdinal(na, nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                var cmp = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}

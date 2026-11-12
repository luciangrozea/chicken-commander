using System.Globalization;

namespace ChickenCommander.Core.FileSystem;

public static class AttributeFormatter
{
    public static string Format(FileAttributes a) => string.Concat(
        a.HasFlag(FileAttributes.ReadOnly) ? "r" : "-",
        a.HasFlag(FileAttributes.Archive) ? "a" : "-",
        a.HasFlag(FileAttributes.Hidden) ? "h" : "-",
        a.HasFlag(FileAttributes.System) ? "s" : "-");
}

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Exact byte count with thousands separators, e.g. "1 234 567".</summary>
    public static string Exact(long bytes) =>
        bytes.ToString("#,0", new NumberFormatInfo { NumberGroupSeparator = " " });

    /// <summary>Human readable size, e.g. "1.5 MB".</summary>
    public static string Human(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value.ToString(value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##", CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}

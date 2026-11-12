using System.Text;

namespace ChickenCommander.Core.Viewer;

/// <summary>
/// Random access to the lines (text mode) or 16-byte rows (hex mode) of a file of any size.
/// Only a line-offset index is kept in memory; content is read on demand, so multi-GB logs open instantly-ish.
/// </summary>
public sealed class FileDocument : IDisposable
{
    public const int HexBytesPerLine = 16;
    private const int MaxLineBytes = 64 * 1024; // very long lines are cut for display

    private readonly FileStream stream;
    private readonly List<long> lineStarts = [];
    private readonly object gate = new();

    private FileDocument(string path)
    {
        Path = path;
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        Length = stream.Length;
        var head = new byte[Math.Min(8192, Length)];
        stream.ReadExactly(head);
        IsBinary = TextDetector.IsBinary(head);
        Encoding = TextDetector.DetectEncoding(head);
        BomLength = TextDetector.BomLength(head, out _);
    }

    public string Path { get; }
    public long Length { get; }
    public bool IsBinary { get; }
    public Encoding Encoding { get; }
    public int BomLength { get; }

    public int LineCount => lineStarts.Count;
    public long HexLineCount => (Length + HexBytesPerLine - 1) / HexBytesPerLine;

    /// <summary>Opens a file and indexes its lines (cancellable for huge files).</summary>
    public static FileDocument Open(string path, CancellationToken ct = default)
    {
        var doc = new FileDocument(path);
        try
        {
            doc.BuildIndex(ct);
            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    private void BuildIndex(CancellationToken ct)
    {
        // UTF-16/32 newlines are multi-byte; find the '\n' code unit at the right alignment.
        var unit = Encoding switch { UnicodeEncoding => 2, UTF32Encoding => 4, _ => 1 };
        var bigEndian = Encoding is UnicodeEncoding && Encoding.GetPreamble() is [0xFE, ..];
        lineStarts.Add(BomLength);
        if (Length == BomLength) return;

        var buffer = new byte[1024 * 1024];
        stream.Position = 0;
        long position = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (unit == 1)
            {
                var span = buffer.AsSpan(0, read);
                var offset = 0;
                int idx;
                while ((idx = span[offset..].IndexOf((byte)'\n')) >= 0)
                {
                    offset += idx + 1;
                    lineStarts.Add(position + offset);
                }
            }
            else
            {
                // Reads are 1 MB, a multiple of 2 and 4, so code units never straddle buffers.
                for (var i = (int)((unit - position % unit) % unit); i + unit <= read; i += unit)
                {
                    var isNewline = unit == 2
                        ? (bigEndian ? buffer[i] == 0 && buffer[i + 1] == '\n' : buffer[i] == '\n' && buffer[i + 1] == 0)
                        : buffer[i] == '\n' && buffer[i + 1] == 0 && buffer[i + 2] == 0 && buffer[i + 3] == 0;
                    if (isNewline) lineStarts.Add(position + i + unit);
                }
            }
            position += read;
        }
        // A trailing newline does not start another (empty) line.
        if (lineStarts.Count > 1 && lineStarts[^1] >= Length) lineStarts.RemoveAt(lineStarts.Count - 1);
    }

    /// <summary>Text of line <paramref name="index"/> (0-based) without its line break.</summary>
    public string GetLine(int index)
    {
        var start = lineStarts[index];
        var end = index + 1 < lineStarts.Count ? lineStarts[index + 1] : Length;
        var count = (int)Math.Min(end - start, MaxLineBytes);
        var bytes = ReadAt(start, count);
        var text = Encoding.GetString(bytes);
        return text.TrimEnd('\n', '\r', '\0');
    }

    /// <summary>One hex row: "00000010  48 65 6C 6C 6F …  Hello…".</summary>
    public string GetHexLine(long index)
    {
        var offset = index * HexBytesPerLine;
        var bytes = ReadAt(offset, (int)Math.Min(HexBytesPerLine, Length - offset));
        return HexFormatter.FormatLine(offset, bytes);
    }

    /// <summary>Finds the next line (wrapping around) that contains <paramref name="text"/>; -1 if none.</summary>
    public int FindLine(string text, int startIndex, bool matchCase, CancellationToken ct = default)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var n = 0; n < LineCount; n++)
        {
            if ((n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            var i = (startIndex + n) % LineCount;
            if (GetLine(i).Contains(text, comparison)) return i;
        }
        return -1;
    }

    /// <summary>Whole text, for small files shown in a regular text box.</summary>
    public string ReadAllText()
    {
        var bytes = ReadAt(BomLength, (int)Math.Min(Length - BomLength, int.MaxValue));
        return Encoding.GetString(bytes);
    }

    private byte[] ReadAt(long offset, int count)
    {
        var bytes = new byte[Math.Max(0, count)];
        lock (gate)
        {
            stream.Position = offset;
            var total = 0;
            while (total < bytes.Length)
            {
                var n = stream.Read(bytes, total, bytes.Length - total);
                if (n == 0) break;
                total += n;
            }
            if (total < bytes.Length) Array.Resize(ref bytes, total); // file shrank meanwhile
        }
        return bytes;
    }

    public void Dispose() => stream.Dispose();
}

public static class HexFormatter
{
    public static string FormatLine(long offset, ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(80);
        sb.Append(offset.ToString("X8")).Append("  ");
        for (var i = 0; i < FileDocument.HexBytesPerLine; i++)
        {
            if (i == 8) sb.Append(' ');
            sb.Append(i < bytes.Length ? bytes[i].ToString("X2") : "  ").Append(' ');
        }
        sb.Append(' ');
        foreach (var b in bytes) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        return sb.ToString();
    }
}

using System.Text;

namespace ChickenCommander.Core.Viewer;

/// <summary>
/// Random access to the lines (text mode) or 16-byte rows (hex mode) of a file of any size.
/// Content is read on demand. The line index is sparse: it keeps one checkpoint every 64 lines (or 256 KB),
/// so a 10 GB log costs a few MB of memory instead of gigabytes; the lines between two checkpoints are found
/// again when that part of the file is shown (and kept in a small cache).
/// </summary>
public sealed class FileDocument : IDisposable
{
    public const int HexBytesPerLine = 16;
    private const int MaxLineBytes = 64 * 1024; // very long lines are cut for display
    private const int LinesPerCheckpoint = 64;
    private const long BytesPerCheckpoint = 256 * 1024;
    private const int BlockCacheSize = 64;
    private const int ChunkSize = 1024 * 1024;

    private readonly FileStream stream;
    private readonly object gate = new();
    private readonly int unit;          // bytes per code unit: 1 (UTF-8/ANSI), 2 (UTF-16), 4 (UTF-32)
    private readonly bool bigEndian;

    // Sparse line index: checkpointOffsets[i] is where line checkpointLines[i] starts.
    private readonly List<long> checkpointOffsets = [];
    private readonly List<int> checkpointLines = [];
    private int lineCount;

    // Line starts of recently shown blocks (block = the lines between two checkpoints).
    private readonly Dictionary<int, long[]> blockCache = [];
    private readonly Queue<int> blockOrder = new();

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
        unit = Encoding switch { UnicodeEncoding => 2, UTF32Encoding => 4, _ => 1 };
        bigEndian = Encoding is UnicodeEncoding && Encoding.GetPreamble() is [0xFE, ..];
    }

    public string Path { get; }
    public long Length { get; }
    public bool IsBinary { get; }
    public Encoding Encoding { get; }
    public int BomLength { get; }

    /// <summary>True once the lines have been counted (text mode needs it, hex mode does not).</summary>
    public bool IsIndexed { get; private set; }

    /// <summary>True when the file has more lines than can be shown (over 2 billion); the rest is cut off.</summary>
    public bool IsTruncated { get; private set; }

    public int LineCount => lineCount;
    public long HexLineCount => (Length + HexBytesPerLine - 1) / HexBytesPerLine;

    /// <summary>Opens a file and indexes its lines (cancellable for huge files).</summary>
    public static FileDocument Open(string path, CancellationToken ct = default) => Open(path, indexLines: true, ct);

    /// <summary>
    /// Opens a file. Without <paramref name="indexLines"/> only the first bytes are read (type and encoding),
    /// which is enough for hex mode; call <see cref="EnsureIndexed"/> before using lines.
    /// </summary>
    public static FileDocument Open(string path, bool indexLines, CancellationToken ct = default)
    {
        var doc = new FileDocument(path);
        try
        {
            if (indexLines) doc.EnsureIndexed(ct);
            return doc;
        }
        catch
        {
            doc.Dispose();
            throw;
        }
    }

    /// <summary>Counts the lines and records the checkpoints. Does nothing the second time.</summary>
    public void EnsureIndexed(CancellationToken ct = default)
    {
        if (IsIndexed) return;
        checkpointOffsets.Clear();
        checkpointLines.Clear();
        checkpointOffsets.Add(BomLength);
        checkpointLines.Add(0);
        var count = 1;
        var lastStart = (long)BomLength;
        var lastCheckpoint = (long)BomLength;
        var sinceCheckpoint = 0;
        var truncated = false;

        if (Length > BomLength)
        {
            ScanLineStarts(BomLength, Length, start =>
            {
                if (count == int.MaxValue)
                {
                    truncated = true;
                    return false;
                }
                count++;
                lastStart = start;
                if (++sinceCheckpoint >= LinesPerCheckpoint || start - lastCheckpoint >= BytesPerCheckpoint)
                {
                    checkpointOffsets.Add(start);
                    checkpointLines.Add(count - 1);
                    lastCheckpoint = start;
                    sinceCheckpoint = 0;
                }
                return true;
            }, ct);
        }

        // A trailing newline does not start another (empty) line.
        if (count > 1 && lastStart >= Length)
        {
            count--;
            if (checkpointOffsets[^1] >= Length)
            {
                checkpointOffsets.RemoveAt(checkpointOffsets.Count - 1);
                checkpointLines.RemoveAt(checkpointLines.Count - 1);
            }
        }
        lineCount = count;
        IsTruncated = truncated;
        IsIndexed = true;
    }

    /// <summary>Text of line <paramref name="index"/> (0-based) without its line break.</summary>
    public string GetLine(int index)
    {
        if (!IsIndexed) throw new InvalidOperationException("The lines have not been indexed.");
        if ((uint)index >= (uint)lineCount) throw new ArgumentOutOfRangeException(nameof(index));
        var block = BlockOf(index);
        var starts = BlockStarts(block);
        var local = index - checkpointLines[block];
        var start = starts[local];
        var end = local + 1 < starts.Length ? starts[local + 1] : BlockEnd(block);
        var count = (int)Math.Min(end - start, MaxLineBytes);
        var bytes = ReadAt(start, count);
        var text = Encoding.GetString(bytes);
        return text.TrimEnd('\n', '\r', '\0');
    }

    /// <summary>One hex row: "00000010  48 65 6C 6C 6F …  Hello…".</summary>
    public string GetHexLine(long index)
    {
        var offset = index * HexBytesPerLine;
        var bytes = ReadAt(offset, (int)Math.Max(0, Math.Min(HexBytesPerLine, Length - offset)));
        return HexFormatter.FormatLine(offset, bytes);
    }

    /// <summary>Finds the next line (wrapping around) that contains <paramref name="text"/>; -1 if none.</summary>
    public int FindLine(string text, int startIndex, bool matchCase, CancellationToken ct = default)
    {
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var n = 0; n < LineCount; n++)
        {
            if ((n & 0x3FF) == 0) ct.ThrowIfCancellationRequested();
            var i = (int)(((long)startIndex + n) % LineCount);
            if (GetLine(i).Contains(text, comparison)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Finds <paramref name="text"/> as raw single-byte characters (what the hex view's right column shows),
    /// starting at <paramref name="startOffset"/> and wrapping around. Returns the byte offset or -1.
    /// </summary>
    public long FindBytes(string text, long startOffset, bool matchCase, CancellationToken ct = default)
    {
        if (text.Length == 0 || Length == 0) return -1;
        var pattern = Encoding.Latin1.GetBytes(matchCase ? text : text.ToLowerInvariant());
        if (pattern.Length > Length) return -1;
        startOffset = Math.Clamp(startOffset, 0, Length);
        var hit = FindBytesIn(pattern, startOffset, Length, matchCase, ct);
        if (hit < 0 && startOffset > 0) hit = FindBytesIn(pattern, 0, Math.Min(Length, startOffset + pattern.Length - 1), matchCase, ct);
        return hit;
    }

    private long FindBytesIn(byte[] pattern, long from, long to, bool matchCase, CancellationToken ct)
    {
        var position = from;
        while (position < to)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = ReadAt(position, (int)Math.Min(ChunkSize, to - position));
            if (bytes.Length < pattern.Length) return -1;
            if (!matchCase)
            {
                for (var i = 0; i < bytes.Length; i++)
                    if (bytes[i] is >= (byte)'A' and <= (byte)'Z') bytes[i] += 32;
            }
            var idx = bytes.AsSpan().IndexOf(pattern);
            if (idx >= 0) return position + idx;
            if (position + bytes.Length >= to) return -1;
            position += bytes.Length - (pattern.Length - 1); // overlap so a match across chunks is found
        }
        return -1;
    }

    /// <summary>Whole text, for small files shown in a regular text box.</summary>
    public string ReadAllText()
    {
        var bytes = ReadAt(BomLength, (int)Math.Min(Length - BomLength, int.MaxValue));
        return Encoding.GetString(bytes);
    }

    // ---- Sparse index helpers ------------------------------------------------------------------

    private int BlockOf(int line)
    {
        var i = checkpointLines.BinarySearch(line);
        return i >= 0 ? i : ~i - 1;
    }

    private long BlockEnd(int block) => block + 1 < checkpointOffsets.Count ? checkpointOffsets[block + 1] : Length;

    private int BlockLineCount(int block) =>
        (block + 1 < checkpointLines.Count ? checkpointLines[block + 1] : lineCount) - checkpointLines[block];

    /// <summary>Start offsets of the lines of one block, read from the file the first time.</summary>
    private long[] BlockStarts(int block)
    {
        lock (blockCache)
        {
            if (blockCache.TryGetValue(block, out var cached)) return cached;
        }
        var lines = BlockLineCount(block);
        var starts = new long[lines];
        starts[0] = checkpointOffsets[block];
        var n = 1;
        if (lines > 1)
        {
            ScanLineStarts(starts[0], BlockEnd(block), start =>
            {
                starts[n++] = start;
                return n < lines;
            }, CancellationToken.None);
        }
        lock (blockCache)
        {
            blockCache[block] = starts;
            blockOrder.Enqueue(block);
            if (blockOrder.Count > BlockCacheSize) blockCache.Remove(blockOrder.Dequeue());
        }
        return starts;
    }

    /// <summary>
    /// Calls <paramref name="onLineStart"/> with the offset right after each line break in [from, to)
    /// until it returns false. <paramref name="from"/> must be at a code unit boundary.
    /// </summary>
    private void ScanLineStarts(long from, long to, Func<long, bool> onLineStart, CancellationToken ct)
    {
        var buffer = new byte[ChunkSize]; // a multiple of 2 and 4, so code units never straddle chunks
        var position = from;
        while (position < to)
        {
            ct.ThrowIfCancellationRequested();
            var read = ReadInto(position, buffer, (int)Math.Min(buffer.Length, to - position));
            if (read == 0) return;
            if (unit == 1)
            {
                var span = buffer.AsSpan(0, read);
                var offset = 0;
                int idx;
                while ((idx = span[offset..].IndexOf((byte)'\n')) >= 0)
                {
                    offset += idx + 1;
                    if (!onLineStart(position + offset)) return;
                }
            }
            else
            {
                for (var i = (int)((unit - position % unit) % unit); i + unit <= read; i += unit)
                {
                    var isNewline = unit == 2
                        ? (bigEndian ? buffer[i] == 0 && buffer[i + 1] == '\n' : buffer[i] == '\n' && buffer[i + 1] == 0)
                        : buffer[i] == '\n' && buffer[i + 1] == 0 && buffer[i + 2] == 0 && buffer[i + 3] == 0;
                    if (isNewline && !onLineStart(position + i + unit)) return;
                }
            }
            position += read;
        }
    }

    private int ReadInto(long offset, byte[] buffer, int count)
    {
        lock (gate)
        {
            stream.Position = offset;
            var total = 0;
            while (total < count)
            {
                var n = stream.Read(buffer, total, count - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }
    }

    private byte[] ReadAt(long offset, int count)
    {
        var bytes = new byte[Math.Max(0, count)];
        var total = ReadInto(offset, bytes, bytes.Length);
        if (total < bytes.Length) Array.Resize(ref bytes, total); // file shrank meanwhile
        return bytes;
    }

    public void Dispose()
    {
        lock (gate) stream.Dispose();
    }
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

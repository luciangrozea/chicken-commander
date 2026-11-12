namespace ChickenCommander.Core.Tools;

public enum DiffKind { Equal, Deleted, Inserted, Modified }

/// <summary>One row of a side-by-side comparison. Line numbers are 1-based; null where a side has no line.</summary>
public sealed record DiffLine(DiffKind Kind, int? LeftNumber, string? Left, int? RightNumber, string? Right);

public sealed record DiffOptions(bool IgnoreCase = false, bool IgnoreWhitespace = false);

/// <summary>Line-based comparison using Myers' O(ND) algorithm.</summary>
public static class TextDiff
{
    /// <summary>Above this many differences the tail is reported as one changed block (keeps memory bounded).</summary>
    private const int MaxEditDistance = 4_000;

    public static IReadOnlyList<DiffLine> Compare(IReadOnlyList<string> left, IReadOnlyList<string> right, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
        // Map lines to integers once; comparisons in the inner loop are then cheap.
        var ids = new Dictionary<string, int>();
        int Id(string s)
        {
            var key = Normalize(s, options);
            if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
            return id;
        }
        var a = left.Select(Id).ToArray();
        var b = right.Select(Id).ToArray();

        // Common prefix and suffix are cheap to strip and are the usual case.
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        var ops = new List<(char Op, int A, int B)>(); // '=', '-', '+' with indexes into a / b
        for (var i = 0; i < prefix; i++) ops.Add(('=', i, i));
        ops.AddRange(Myers(a[prefix..^suffix], b[prefix..^suffix], prefix));
        for (var i = 0; i < suffix; i++) ops.Add(('=', a.Length - suffix + i, b.Length - suffix + i));

        return ToRows(ops, left, right);
    }

    /// <summary>True if the two files have identical bytes.</summary>
    public static bool FilesEqual(string pathA, string pathB)
    {
        var fa = new FileInfo(pathA);
        var fb = new FileInfo(pathB);
        if (fa.Length != fb.Length) return false;
        return FirstDifference(pathA, pathB) is null;
    }

    /// <summary>Offset of the first differing byte, or null if the files are identical.</summary>
    public static long? FirstDifference(string pathA, string pathB, CancellationToken ct = default)
    {
        const int size = 256 * 1024;
        using var sa = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        using var sb = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);
        var ba = new byte[size];
        var bb = new byte[size];
        long offset = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var na = sa.ReadAtLeast(ba, size, throwOnEndOfStream: false);
            var nb = sb.ReadAtLeast(bb, size, throwOnEndOfStream: false);
            var common = Math.Min(na, nb);
            var mismatch = ba.AsSpan(0, common).CommonPrefixLength(bb.AsSpan(0, common));
            if (mismatch < common) return offset + mismatch;
            if (na != nb) return offset + common;
            if (na == 0) return null;
            offset += na;
        }
    }

    private static string Normalize(string s, DiffOptions o)
    {
        if (o.IgnoreWhitespace) s = string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        if (o.IgnoreCase) s = s.ToUpperInvariant();
        return s;
    }

    private static List<(char, int, int)> Myers(int[] a, int[] b, int baseIndex)
    {
        int n = a.Length, m = b.Length;
        var result = new List<(char, int, int)>();
        if (n == 0)
        {
            for (var j = 0; j < m; j++) result.Add(('+', -1, baseIndex + j));
            return result;
        }
        if (m == 0)
        {
            for (var i = 0; i < n; i++) result.Add(('-', baseIndex + i, -1));
            return result;
        }

        var max = n + m;
        var offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        var found = false;
        var limit = Math.Min(max, MaxEditDistance);

        for (var d = 0; d <= limit && !found; d++)
        {
            // Save only the diagonals round d can read (-d-1..d+1): memory grows with D², not D·(N+M).
            trace.Add(v.AsSpan(offset - d - 1, 2 * d + 3).ToArray());
            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = true; break; }
            }
        }

        if (!found)
        {
            // Too different to align precisely: show everything as removed then added.
            for (var i = 0; i < n; i++) result.Add(('-', baseIndex + i, -1));
            for (var j = 0; j < m; j++) result.Add(('+', -1, baseIndex + j));
            return result;
        }

        // Backtrack from (n, m) through the saved V arrays.
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            var shift = d + 1; // vd[k + shift] is diagonal k as saved at the start of round d
            var k = cx - cy;
            var prevK = k == -d || (k != d && vd[k - 1 + shift] < vd[k + 1 + shift]) ? k + 1 : k - 1;
            var prevX = vd[prevK + shift];
            var prevY = prevX - prevK;
            while (cx > prevX && cy > prevY)
            {
                result.Add(('=', baseIndex + cx - 1, baseIndex + cy - 1));
                cx--;
                cy--;
            }
            if (d > 0)
            {
                if (cx == prevX) result.Add(('+', -1, baseIndex + prevY));
                else result.Add(('-', baseIndex + prevX, -1));
            }
            cx = prevX;
            cy = prevY;
        }
        result.Reverse();
        return result;
    }

    /// <summary>Turns the edit script into rows; a block of deletions followed by insertions becomes "Modified" pairs.</summary>
    private static List<DiffLine> ToRows(List<(char Op, int A, int B)> ops, IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var rows = new List<DiffLine>(ops.Count);
        var i = 0;
        while (i < ops.Count)
        {
            var (op, ai, bi) = ops[i];
            if (op == '=')
            {
                rows.Add(new DiffLine(DiffKind.Equal, ai + 1, left[ai], bi + 1, right[bi]));
                i++;
                continue;
            }

            var deleted = new List<int>();
            var inserted = new List<int>();
            while (i < ops.Count && ops[i].Op != '=')
            {
                if (ops[i].Op == '-') deleted.Add(ops[i].A);
                else inserted.Add(ops[i].B);
                i++;
            }
            var pairs = Math.Min(deleted.Count, inserted.Count);
            for (var p = 0; p < pairs; p++)
                rows.Add(new DiffLine(DiffKind.Modified, deleted[p] + 1, left[deleted[p]], inserted[p] + 1, right[inserted[p]]));
            foreach (var d in deleted.Skip(pairs))
                rows.Add(new DiffLine(DiffKind.Deleted, d + 1, left[d], null, null));
            foreach (var n in inserted.Skip(pairs))
                rows.Add(new DiffLine(DiffKind.Inserted, null, null, n + 1, right[n]));
        }
        return rows;
    }
}

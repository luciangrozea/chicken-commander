using System.Text;

namespace ChickenCommander.Core.Viewer;

/// <summary>Guesses whether bytes are text, and in which encoding.</summary>
public static class TextDetector
{
    /// <summary>True when the sample looks binary: NUL bytes without a UTF-16/32 byte order mark.</summary>
    public static bool IsBinary(ReadOnlySpan<byte> sample)
    {
        if (BomLength(sample, out var enc) > 0 && enc is UnicodeEncoding or UTF32Encoding) return false;
        var controls = 0;
        foreach (var b in sample)
        {
            if (b == 0) return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B)) controls++;
        }
        return sample.Length > 0 && controls * 10 > sample.Length; // more than 10% control characters
    }

    /// <summary>BOM first; otherwise UTF-8 if the bytes are valid UTF-8, else Latin-1.</summary>
    public static Encoding DetectEncoding(ReadOnlySpan<byte> sample)
    {
        if (BomLength(sample, out var bomEncoding) > 0) return bomEncoding!;
        return IsValidUtf8(sample) ? new UTF8Encoding(false) : Encoding.Latin1;
    }

    /// <summary>Length of a byte order mark at the start of <paramref name="data"/> (0 if none).</summary>
    public static int BomLength(ReadOnlySpan<byte> data, out Encoding? encoding)
    {
        encoding = null;
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0 && data[3] == 0) { encoding = new UTF32Encoding(false, true); return 4; }
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) { encoding = new UTF8Encoding(true); return 3; }
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { encoding = new UnicodeEncoding(false, true); return 2; }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { encoding = new UnicodeEncoding(true, true); return 2; }
        return 0;
    }

    /// <summary>Validates UTF-8; a sequence cut off at the end of the sample is accepted.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var b = data[i];
            int extra;
            if (b < 0x80) { i++; continue; }
            if ((b & 0xE0) == 0xC0 && b >= 0xC2) extra = 1;
            else if ((b & 0xF0) == 0xE0) extra = 2;
            else if ((b & 0xF8) == 0xF0 && b <= 0xF4) extra = 3;
            else return false;

            for (var k = 1; k <= extra; k++)
            {
                if (i + k >= data.Length) return true; // truncated at sample end
                if ((data[i + k] & 0xC0) != 0x80) return false;
            }
            i += extra + 1;
        }
        return true;
    }

    public static string Describe(Encoding e) => e switch
    {
        UTF8Encoding u => u.GetPreamble().Length > 0 ? "UTF-8 (BOM)" : "UTF-8",
        UnicodeEncoding u => u.GetPreamble() is [0xFE, ..] ? "UTF-16 BE" : "UTF-16 LE",
        UTF32Encoding => "UTF-32",
        _ => e.WebName.ToUpperInvariant(),
    };
}

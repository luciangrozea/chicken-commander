using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;

namespace ChickenCommander.Services;

/// <summary>
/// Files on the Windows clipboard, in the same format Explorer uses (a file drop list plus
/// "Preferred DropEffect"), so Ctrl+C / Ctrl+X / Ctrl+V work between Chicken Commander and Explorer both ways.
/// </summary>
public static class FileClipboard
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private const int DropEffectCopy = 1, DropEffectMove = 2, DropEffectLink = 4;

    public sealed record Content(IReadOnlyList<string> Paths, bool IsCut);

    /// <exception cref="ExternalException">The clipboard is in use by another program.</exception>
    public static void Put(IReadOnlyList<string> paths, bool cut)
    {
        var list = new StringCollection();
        list.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(cut ? DropEffectMove : DropEffectCopy | DropEffectLink)));
        Retry(() => Clipboard.SetDataObject(data, copy: true));
    }

    /// <summary>The files on the clipboard, or null when it holds no files.</summary>
    public static Content? Get()
    {
        IDataObject? data = null;
        Retry(() => data = Clipboard.GetDataObject());
        if (data is null || !data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return null;
        return new Content(paths, IsCut(data));
    }

    public static bool ContainsFiles()
    {
        try { return Clipboard.ContainsFileDropList(); }
        catch (ExternalException) { return false; }
    }

    /// <summary>After a cut is pasted the files are gone from their old place, so the clipboard is emptied (as Explorer does).</summary>
    public static void Clear()
    {
        try { Retry(Clipboard.Clear); }
        catch (ExternalException) { /* another program holds the clipboard; harmless */ }
    }

    private static bool IsCut(IDataObject data)
    {
        try
        {
            var effect = data.GetData(DropEffectFormat) switch
            {
                MemoryStream ms when ms.Length >= 4 => BitConverter.ToInt32(ms.ToArray(), 0),
                byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0),
                _ => DropEffectCopy,
            };
            return (effect & DropEffectMove) != 0 && (effect & DropEffectCopy) == 0;
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The clipboard is shared by all programs; when one has it open, try again briefly.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }
}

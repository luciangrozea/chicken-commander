using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.Services;

/// <summary>Small Windows shell icons for files and folders, cached by extension.</summary>
public static class ShellIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    // These file types carry their own icon, so they are looked up by full path instead of by extension.
    private static readonly HashSet<string> PerFileIcon = new(StringComparer.OrdinalIgnoreCase) { "exe", "ico", "lnk", "url", "cur", "scr" };

    public static ImageSource? For(FileEntry entry)
    {
        if (entry.IsDirectory)
            return Cache.GetOrAdd("<dir>", _ => Load("folder", FileAttributeDirectory, useAttributes: true));

        // Files inside archives or on servers have no local path to read an icon from: use the type's icon.
        if (PerFileIcon.Contains(entry.Extension) && !entry.FullPath.Contains("://", StringComparison.Ordinal) && File.Exists(entry.FullPath))
            return Cache.GetOrAdd(entry.FullPath, p => Load(p, FileAttributeNormal, useAttributes: false));

        var key = "." + entry.Extension;
        return Cache.GetOrAdd(key, k => Load("file" + k, FileAttributeNormal, useAttributes: true));
    }

    private static ImageSource? Load(string path, uint attributes, bool useAttributes)
    {
        var info = new SHFILEINFO();
        var flags = ShgfiIcon | ShgfiSmallIcon | (useAttributes ? ShgfiUseFileAttributes : 0);
        var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

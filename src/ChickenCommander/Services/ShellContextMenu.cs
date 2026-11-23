using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ChickenCommander.Services;

/// <summary>
/// The real Windows Explorer right-click menu (the classic one, "Show more options" on Windows 11) for files and
/// folders: Open with, Send to, Properties and every shell extension the user has installed (7-Zip, antivirus…).
/// Verbs the app does itself (copy, cut, paste, rename, delete) are handed back so the panels stay in sync.
/// </summary>
public static class ShellContextMenu
{
    /// <summary>Shows the menu for <paramref name="paths"/> (all in one folder) at a screen point.</summary>
    /// <param name="handleVerb">Called with the canonical verb ("copy", "rename"…) before the shell runs it;
    /// return true when the app handled it.</param>
    /// <returns>false when the shell could not build a menu (the caller can fall back to its own).</returns>
    public static bool Show(Window owner, IReadOnlyList<string> paths, Point screenPoint, Func<string, bool> handleVerb)
    {
        if (paths.Count == 0) return false;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var pidls = new List<IntPtr>();
        IntPtr parentPtr = IntPtr.Zero, menuPtr = IntPtr.Zero, hmenu = IntPtr.Zero;
        HwndSource? source = null;
        HwndSourceHook? hook = null;
        object? contextMenu = null;
        try
        {
            foreach (var path in paths)
            {
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return false;
                pidls.Add(pidl);
            }
            var shellFolderId = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidls[0], ref shellFolderId, out parentPtr, out _) != 0) return false;
            var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
            var children = pidls.Select(ILFindLastID).ToArray();
            var contextMenuId = typeof(IContextMenu).GUID;
            if (parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref contextMenuId, IntPtr.Zero, out menuPtr) != 0 || menuPtr == IntPtr.Zero)
                return false;
            contextMenu = Marshal.GetObjectForIUnknown(menuPtr);
            var menu = (IContextMenu)contextMenu;

            hmenu = CreatePopupMenu();
            var flags = CmfNormal | CmfExplore | CmfCanRename;
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) flags |= CmfExtendedVerbs;
            if (menu.QueryContextMenu(hmenu, 0, FirstCommand, LastCommand, flags) < 0) return false;

            // "Send to", "Open with" and icons are filled in on demand through these messages.
            source = HwndSource.FromHwnd(hwnd);
            hook = (IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg is WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar)
                {
                    if (contextMenu is IContextMenu3 cm3 && cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
                    {
                        handled = true;
                        return result;
                    }
                    if (contextMenu is IContextMenu2 cm2 && cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
                    {
                        handled = true;
                        return msg == WmInitMenuPopup ? IntPtr.Zero : new IntPtr(1);
                    }
                }
                return IntPtr.Zero;
            };
            source?.AddHook(hook);

            var command = TrackPopupMenuEx(hmenu, TpmReturnCmd | TpmRightButton, (int)screenPoint.X, (int)screenPoint.Y, hwnd, IntPtr.Zero);
            if (command < FirstCommand) return true; // closed without a choice

            var offset = (uint)(command - FirstCommand);
            var verb = GetVerb(menu, offset);
            if (verb is not null && handleVerb(verb.ToLowerInvariant())) return true;

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CmicMaskUnicode | CmicMaskPtInvoke,
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                lpDirectoryW = Path.GetDirectoryName(paths[0]),
                nShow = SwShowNormal,
                ptInvoke = new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y },
            };
            menu.InvokeCommand(ref info);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (hook is not null) source?.RemoveHook(hook);
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (contextMenu is not null) Marshal.ReleaseComObject(contextMenu);
            if (menuPtr != IntPtr.Zero) Marshal.Release(menuPtr);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var pidl in pidls) ILFree(pidl);
        }
    }

    /// <summary>Opens the Windows Properties dialog of a file or folder.</summary>
    public static void ShowProperties(Window owner, string path)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SeeMaskInvokeIdList,
            hwnd = new WindowInteropHelper(owner).Handle,
            lpVerb = "properties",
            lpFile = path,
            nShow = SwShowNormal,
        };
        ShellExecuteEx(ref info);
    }

    private static string? GetVerb(IContextMenu menu, uint offset)
    {
        var buffer = Marshal.AllocHGlobal(512 * 2);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            return menu.GetCommandString((UIntPtr)offset, GcsVerbW, IntPtr.Zero, buffer, 512) == 0
                ? Marshal.PtrToStringUni(buffer)
                : null;
        }
        catch (Exception ex) when (ex is COMException or NotImplementedException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---- Win32 / COM -------------------------------------------------------------------------

    private const uint FirstCommand = 1, LastCommand = 0x7FFF;
    private const uint CmfNormal = 0x0, CmfExplore = 0x4, CmfCanRename = 0x10, CmfExtendedVerbs = 0x100;
    private const uint TpmReturnCmd = 0x100, TpmRightButton = 0x2;
    private const uint GcsVerbW = 0x4;
    private const uint CmicMaskUnicode = 0x4000, CmicMaskPtInvoke = 0x20000000;
    private const uint SeeMaskInvokeIdList = 0xC;
    private const int SwShowNormal = 1;
    private const int WmInitMenuPopup = 0x117, WmDrawItem = 0x2B, WmMeasureItem = 0x2C, WmMenuChar = 0x120;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPStr)] public string? lpTitle;
        public IntPtr lpVerbW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParametersW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint count, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr newPidl);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, int maxChars);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, int maxChars);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, int maxChars);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hmenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);
}

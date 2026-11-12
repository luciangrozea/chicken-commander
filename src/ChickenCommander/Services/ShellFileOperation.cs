using System.Runtime.InteropServices;

namespace ChickenCommander.Services;

/// <summary>
/// Delete and rename through the Windows shell (SHFileOperation), for Recycle Bin support and Explorer-style undo.
/// Copy and move use the app's own engine (see ChickenCommander.Core.Operations.FileTransfer).
/// </summary>
public static class ShellFileOperation
{
    /// <returns>false if the user cancelled or the operation failed.</returns>
    public static bool Delete(IntPtr owner, IEnumerable<string> paths, bool toRecycleBin) =>
        Run(owner, FoDelete, paths, null, (ushort)(FofNoConfirmation | FofWantNukeWarning | (toRecycleBin ? FofAllowUndo : 0)));

    public static bool Rename(IntPtr owner, string path, string newPath) =>
        Run(owner, FoRename, [path], newPath, FofAllowUndo);

    private static bool Run(IntPtr owner, uint func, IEnumerable<string> from, string? to, ushort flags)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = func,
            pFrom = string.Join('\0', from) + "\0\0",
            pTo = to is null ? null : to + "\0\0",
            fFlags = flags,
        };
        var result = SHFileOperation(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    private const uint FoDelete = 3, FoRename = 4;
    private const ushort FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}

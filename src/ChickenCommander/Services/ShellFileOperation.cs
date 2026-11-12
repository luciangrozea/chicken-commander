using System.Runtime.InteropServices;

namespace ChickenCommander.Services;

/// <summary>
/// Copy / move / delete / rename through the Windows shell (SHFileOperation). This gives native progress,
/// overwrite prompts and Recycle Bin support for free. A custom background operation queue replaces it in v0.2.
/// </summary>
public static class ShellFileOperation
{
    /// <returns>false if the user cancelled or the operation failed.</returns>
    public static bool Copy(IntPtr owner, IEnumerable<string> sources, string destination) =>
        Run(owner, FoCopy, sources, destination, FofAllowUndo | FofNoConfirmMkdir);

    public static bool Move(IntPtr owner, IEnumerable<string> sources, string destination) =>
        Run(owner, FoMove, sources, destination, FofAllowUndo | FofNoConfirmMkdir);

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

    private const uint FoMove = 1, FoCopy = 2, FoDelete = 3, FoRename = 4;
    private const ushort FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoConfirmMkdir = 0x200, FofWantNukeWarning = 0x4000;

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

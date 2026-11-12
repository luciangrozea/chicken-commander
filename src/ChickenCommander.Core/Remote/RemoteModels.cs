using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.Core.Remote;

public enum RemoteProtocol { Ftp, FtpsExplicit, Sftp }

/// <summary>A saved server connection. The password is stored encrypted by the app (Windows DPAPI), or not at all.</summary>
public sealed record RemoteConnection
{
    public required string Name { get; init; }
    public RemoteProtocol Protocol { get; init; } = RemoteProtocol.Sftp;
    public required string Host { get; init; }
    public int Port { get; init; }
    public string User { get; init; } = string.Empty;
    public string InitialDirectory { get; init; } = "/";
    public string? PrivateKeyFile { get; init; }
    public string? EncryptedPassword { get; init; }

    /// <summary>SHA-256 fingerprint of the SFTP server key accepted by the user (trust on first use).</summary>
    public string? HostKeyFingerprint { get; init; }

    /// <summary>For FTPS: the certificate hash the user accepted when it was not trusted by Windows.</summary>
    public string? AcceptedCertificate { get; init; }

    public int EffectivePort => Port > 0 ? Port : Protocol == RemoteProtocol.Sftp ? 22 : 21;

    public string Scheme => Protocol == RemoteProtocol.Sftp ? "sftp" : "ftp";

    /// <summary>Panel path for a directory on this server, e.g. <c>sftp://My server/home/me</c>.</summary>
    public string ToPanelPath(string remotePath) => $"{Scheme}://{Name}{RemotePath.Normalize(remotePath)}";
}

/// <summary>Asks the user about things the client cannot decide alone (unknown host key, untrusted certificate).</summary>
public interface IRemoteTrust
{
    /// <summary>Unknown or changed SSH host key. Return true to trust it.</summary>
    bool ConfirmHostKey(RemoteConnection connection, string fingerprint, bool changed);

    /// <summary>A TLS certificate Windows does not trust. Return true to accept it for this connection.</summary>
    bool ConfirmCertificate(RemoteConnection connection, string subject, string hash, string problem);
}

/// <summary>What the app needs from an FTP or SFTP library. Paths are absolute Unix-style paths ("/a/b").</summary>
public interface IRemoteClient : IDisposable
{
    void Connect(CancellationToken ct = default);

    bool IsConnected { get; }

    /// <summary>Entries of a directory; <see cref="FileEntry.FullPath"/> is the remote path.</summary>
    IReadOnlyList<FileEntry> List(string path, CancellationToken ct = default);

    bool DirectoryExists(string path);

    /// <summary>Size and date of a file, or null if it does not exist.</summary>
    (long Size, DateTime Modified)? GetFileInfo(string path);

    void Download(string path, Stream destination, Action<long> progress, CancellationToken ct);

    void Upload(Stream source, string path, Action<long> progress, CancellationToken ct);

    void CreateDirectory(string path);

    void DeleteFile(string path);

    /// <summary>Deletes a directory and everything in it.</summary>
    void DeleteDirectory(string path);

    void Rename(string from, string to);
}

public static class RemotePath
{
    /// <summary>"/a//b/" → "/a/b"; "" → "/"; backslashes become slashes.</summary>
    public static string Normalize(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var p in parts)
        {
            if (p == ".") continue;
            if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(p);
        }
        return "/" + string.Join('/', stack);
    }

    public static string Combine(string directory, string name) => Normalize(directory.TrimEnd('/') + "/" + name);

    public static string? Parent(string path)
    {
        var p = Normalize(path);
        if (p == "/") return null;
        var i = p.LastIndexOf('/');
        return i == 0 ? "/" : p[..i];
    }

    public static string Name(string path) => Normalize(path) is var p && p == "/" ? "/" : p[(p.LastIndexOf('/') + 1)..];

    /// <summary>Parses a panel path "sftp://Name/dir" into the connection name and remote path.</summary>
    public static bool TryParsePanelPath(string panelPath, out string scheme, out string name, out string remotePath)
    {
        scheme = name = remotePath = string.Empty;
        var sep = panelPath.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0) return false;
        scheme = panelPath[..sep].ToLowerInvariant();
        if (scheme is not ("ftp" or "sftp")) return false;
        var rest = panelPath[(sep + 3)..].Replace('\\', '/');
        var slash = rest.IndexOf('/');
        name = slash < 0 ? rest : rest[..slash];
        remotePath = Normalize(slash < 0 ? "/" : rest[slash..]);
        return name.Length > 0;
    }
}

/// <summary>A stream wrapper that aborts long library calls when the user cancels (they take no token themselves).</summary>
public sealed class CancellableStream(Stream inner, CancellationToken ct) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        return inner.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ct.ThrowIfCancellationRequested();
        inner.Write(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
}

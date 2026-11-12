using ChickenCommander.Core.FileSystem;
using Renci.SshNet;

namespace ChickenCommander.Core.Remote;

/// <summary>SFTP via SSH.NET. Host keys are verified against the saved fingerprint (trust on first use).</summary>
public sealed class SftpRemoteClient : IRemoteClient
{
    private readonly SftpClient client;

    public SftpRemoteClient(RemoteConnection connection, string? password, IRemoteTrust trust, Action<string>? fingerprintAccepted = null)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(connection.PrivateKeyFile))
        {
            var key = string.IsNullOrEmpty(password)
                ? new PrivateKeyFile(connection.PrivateKeyFile)
                : new PrivateKeyFile(connection.PrivateKeyFile, password); // password = key passphrase here
            methods.Add(new PrivateKeyAuthenticationMethod(connection.User, key));
        }
        if (!string.IsNullOrEmpty(password)) methods.Add(new PasswordAuthenticationMethod(connection.User, password));
        if (methods.Count == 0) methods.Add(new NoneAuthenticationMethod(connection.User));

        client = new SftpClient(new ConnectionInfo(connection.Host, connection.EffectivePort, connection.User, [.. methods]));
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = "SHA256:" + e.FingerPrintSHA256;
            if (string.Equals(connection.HostKeyFingerprint, fingerprint, StringComparison.Ordinal))
            {
                e.CanTrust = true;
                return;
            }
            e.CanTrust = trust.ConfirmHostKey(connection, fingerprint, changed: connection.HostKeyFingerprint is not null);
            if (e.CanTrust) fingerprintAccepted?.Invoke(fingerprint);
        };
    }

    public bool IsConnected => client.IsConnected;

    public void Connect(CancellationToken ct = default) => client.Connect();

    public IReadOnlyList<FileEntry> List(string path, CancellationToken ct = default) =>
        client.ListDirectory(RemotePath.Normalize(path))
            .Where(f => f.Name is not ("." or ".."))
            .Select(f => new FileEntry(
                f.Name,
                RemotePath.Combine(path, f.Name),
                f.IsDirectory,
                f.IsDirectory ? 0 : f.Length,
                f.LastWriteTime,
                (f.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal) | (f.Name.StartsWith('.') ? FileAttributes.Hidden : 0)))
            .ToList();

    public bool DirectoryExists(string path)
    {
        var p = RemotePath.Normalize(path);
        return client.Exists(p) && client.Get(p).IsDirectory;
    }

    public (long Size, DateTime Modified)? GetFileInfo(string path)
    {
        var p = RemotePath.Normalize(path);
        if (!client.Exists(p)) return null;
        var f = client.Get(p);
        return f.IsDirectory ? null : (f.Length, f.LastWriteTime);
    }

    public void Download(string path, Stream destination, Action<long> progress, CancellationToken ct) =>
        client.DownloadFile(RemotePath.Normalize(path), new CancellableStream(destination, ct), bytes => progress((long)bytes));

    public void Upload(Stream source, string path, Action<long> progress, CancellationToken ct) =>
        client.UploadFile(new CancellableStream(source, ct), RemotePath.Normalize(path), canOverride: true, bytes => progress((long)bytes));

    public void CreateDirectory(string path) => client.CreateDirectory(RemotePath.Normalize(path));

    public void DeleteFile(string path) => client.DeleteFile(RemotePath.Normalize(path));

    public void DeleteDirectory(string path)
    {
        var p = RemotePath.Normalize(path);
        foreach (var child in List(p))
        {
            if (child.IsDirectory) DeleteDirectory(child.FullPath);
            else client.DeleteFile(child.FullPath);
        }
        client.DeleteDirectory(p);
    }

    public void Rename(string from, string to) => client.RenameFile(RemotePath.Normalize(from), RemotePath.Normalize(to));

    public void Dispose()
    {
        if (client.IsConnected) client.Disconnect();
        client.Dispose();
    }
}

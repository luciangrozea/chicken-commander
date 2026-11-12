using System.Net.Security;
using ChickenCommander.Core.FileSystem;
using FluentFTP;

namespace ChickenCommander.Core.Remote;

/// <summary>FTP and explicit FTPS (TLS) via FluentFTP.</summary>
public sealed class FtpRemoteClient : IRemoteClient
{
    private readonly FtpClient client;

    public FtpRemoteClient(RemoteConnection connection, string? password, IRemoteTrust trust, Action<string>? certificateAccepted = null)
    {
        var user = string.IsNullOrEmpty(connection.User) ? "anonymous" : connection.User;
        client = new FtpClient(connection.Host, user, password ?? string.Empty, connection.EffectivePort);
        client.Config.ConnectTimeout = 15_000;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;

        if (connection.Protocol == RemoteProtocol.FtpsExplicit)
        {
            client.Config.EncryptionMode = FtpEncryptionMode.Explicit;
            client.ValidateCertificate += (_, e) =>
            {
                if (e.PolicyErrors == SslPolicyErrors.None)
                {
                    e.Accept = true;
                    return;
                }
                var hash = e.Certificate?.GetCertHashString() ?? string.Empty;
                if (hash.Length > 0 && string.Equals(hash, connection.AcceptedCertificate, StringComparison.OrdinalIgnoreCase))
                {
                    e.Accept = true;
                    return;
                }
                e.Accept = trust.ConfirmCertificate(connection, e.Certificate?.Subject ?? "(unknown)", hash, e.PolicyErrors.ToString());
                if (e.Accept) certificateAccepted?.Invoke(hash);
            };
        }
    }

    public bool IsConnected => client.IsConnected;

    public void Connect(CancellationToken ct = default) => client.Connect();

    public IReadOnlyList<FileEntry> List(string path, CancellationToken ct = default) =>
        client.GetListing(RemotePath.Normalize(path))
            .Where(i => i.Name is not ("." or ".."))
            .Select(i =>
            {
                var isDir = i.Type == FtpObjectType.Directory || (i.Type == FtpObjectType.Link && i.LinkObject?.Type == FtpObjectType.Directory);
                return new FileEntry(
                    i.Name,
                    RemotePath.Combine(path, i.Name),
                    isDir,
                    isDir ? 0 : Math.Max(0, i.Size),
                    i.Modified == DateTime.MinValue ? DateTime.Now : i.Modified,
                    (isDir ? FileAttributes.Directory : FileAttributes.Normal) | (i.Name.StartsWith('.') ? FileAttributes.Hidden : 0));
            })
            .ToList();

    public bool DirectoryExists(string path) => client.DirectoryExists(RemotePath.Normalize(path));

    public (long Size, DateTime Modified)? GetFileInfo(string path)
    {
        var p = RemotePath.Normalize(path);
        if (!client.FileExists(p)) return null;
        return (client.GetFileSize(p), client.GetModifiedTime(p));
    }

    public void Download(string path, Stream destination, Action<long> progress, CancellationToken ct)
    {
        if (!client.DownloadStream(new CancellableStream(destination, ct), RemotePath.Normalize(path), 0, p => progress(p.TransferredBytes)))
            throw new IOException($"Download of {path} failed.");
    }

    public void Upload(Stream source, string path, Action<long> progress, CancellationToken ct)
    {
        var status = client.UploadStream(new CancellableStream(source, ct), RemotePath.Normalize(path), FtpRemoteExists.Overwrite, createRemoteDir: true, p => progress(p.TransferredBytes));
        if (status == FtpStatus.Failed) throw new IOException($"Upload of {path} failed.");
    }

    public void CreateDirectory(string path) => client.CreateDirectory(RemotePath.Normalize(path), force: true);

    public void DeleteFile(string path) => client.DeleteFile(RemotePath.Normalize(path));

    public void DeleteDirectory(string path) => client.DeleteDirectory(RemotePath.Normalize(path));

    public void Rename(string from, string to) => client.Rename(RemotePath.Normalize(from), RemotePath.Normalize(to));

    public void Dispose()
    {
        if (client.IsConnected) client.Disconnect();
        client.Dispose();
    }
}

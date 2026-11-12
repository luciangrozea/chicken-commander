using System.Text;
using ChickenCommander.Core.Remote;

namespace ChickenCommander.Core.Tests;

/// <summary>
/// Talks to real FTP / SFTP servers. Opt-in: set CC_TEST_SERVERS=1 and run local test servers
/// (FTP on 127.0.0.1:2121, SFTP on 127.0.0.1:2222, user "demo" / password "demo", with a "pub" folder
/// that contains "welcome.txt"). Skipped otherwise, so CI does not need a network.
/// </summary>
public class RemoteIntegrationTests
{
    /// <summary>A theory that is reported as skipped unless CC_TEST_SERVERS=1.</summary>
    private sealed class ServerTheoryAttribute : TheoryAttribute
    {
        public ServerTheoryAttribute()
        {
            if (Environment.GetEnvironmentVariable("CC_TEST_SERVERS") != "1")
                Skip = "Set CC_TEST_SERVERS=1 and start the local test servers to run this test.";
        }
    }

    private sealed class TrustAll : IRemoteTrust
    {
        public string? Fingerprint { get; private set; }
        public bool ConfirmHostKey(RemoteConnection c, string fingerprint, bool changed) { Fingerprint = fingerprint; return true; }
        public bool ConfirmCertificate(RemoteConnection c, string subject, string hash, string problem) => true;
    }

    public static TheoryData<RemoteProtocol, int> Servers => new() { { RemoteProtocol.Ftp, 2121 }, { RemoteProtocol.Sftp, 2222 } };

    [ServerTheory]
    [MemberData(nameof(Servers))]
    public void Full_round_trip(RemoteProtocol protocol, int port)
    {
        var connection = new RemoteConnection { Name = "local", Protocol = protocol, Host = "127.0.0.1", Port = port, User = "demo" };
        var trust = new TrustAll();
        using IRemoteClient client = protocol == RemoteProtocol.Sftp
            ? new SftpRemoteClient(connection, "demo", trust)
            : new FtpRemoteClient(connection, "demo", trust);
        client.Connect();
        if (protocol == RemoteProtocol.Sftp) Assert.StartsWith("SHA256:", trust.Fingerprint);

        Assert.Contains(client.List("/pub"), e => e.Name == "welcome.txt" && !e.IsDirectory);

        using (var ms = new MemoryStream())
        {
            client.Download("/pub/welcome.txt", ms, _ => { }, CancellationToken.None);
            Assert.Contains("Hello", Encoding.UTF8.GetString(ms.ToArray()));
        }

        var dir = $"/pub/it-{protocol}-{Guid.NewGuid():N}"[..30];
        client.CreateDirectory(dir);
        Assert.True(client.DirectoryExists(dir));
        client.Upload(new MemoryStream("uploaded"u8.ToArray()), dir + "/a.txt", _ => { }, CancellationToken.None);
        Assert.Equal(8, client.GetFileInfo(dir + "/a.txt")!.Value.Size);
        client.Rename(dir + "/a.txt", dir + "/b.txt");
        Assert.Null(client.GetFileInfo(dir + "/a.txt"));
        client.DeleteDirectory(dir);
        Assert.False(client.DirectoryExists(dir));
    }
}

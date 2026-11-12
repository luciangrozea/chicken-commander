using System.IO.Compression;
using System.Text;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;
using ChickenCommander.Core.Remote;
using ChickenCommander.Core.Tools;
using ChickenCommander.Core.Vfs;

namespace ChickenCommander.Core.Tests;

public class ArchiveTests : TempFolderTest
{
    private string MakeSampleTree()
    {
        Write(@"src\readme.txt", "hello");
        Write(@"src\docs\a.txt", "aaa");
        Write(@"src\docs\deep\b.txt", "bbbb");
        Directory.CreateDirectory(P("src", "empty"));
        return P("src");
    }

    [Theory]
    [InlineData(PackFormat.Zip)]
    [InlineData(PackFormat.Tar)]
    [InlineData(PackFormat.TarGz)]
    public async Task Pack_then_browse_then_extract(PackFormat format)
    {
        var tree = MakeSampleTree();
        var archive = P("out" + ArchiveTools.DefaultExtension(format));
        var packed = await ArchiveTools.PackAsync([tree], archive, format, new ScriptedCallbacks());
        Assert.Equal(3, packed.FilesDone);
        Assert.False(File.Exists(archive + ".cc-partial"));

        var provider = new ArchiveProvider();
        Assert.True(provider.CanHandle(archive + @"\src\docs"));
        Assert.Equal(["src"], provider.List(archive).Select(e => e.Name));
        Assert.Equal(["a.txt", "deep"], provider.List(archive + @"\src\docs").Select(e => e.Name).Order());
        Assert.True(provider.DirectoryExists(archive + @"\src\empty"));
        Assert.Equal(P("src.zip".Replace("src.zip", "")).TrimEnd('\\'), provider.GetParent(archive));

        var index = provider.GetIndex(archive);
        var result = await ArchiveTools.ExtractAsync(archive, [@"src\docs"], P("x"), index, new ScriptedCallbacks());
        Assert.Equal(2, result.FilesDone);
        Assert.Equal("bbbb", File.ReadAllText(P("x", "docs", "deep", "b.txt")));
        Assert.False(File.Exists(P("x", "readme.txt")));
    }

    [Fact]
    public async Task Extract_whole_archive_and_single_file()
    {
        var archive = P("a.zip");
        await ArchiveTools.PackAsync([MakeSampleTree()], archive, PackFormat.Zip, new ScriptedCallbacks());
        var index = new ArchiveProvider().GetIndex(archive);

        await ArchiveTools.ExtractAsync(archive, [""], P("all"), index, new ScriptedCallbacks());
        Assert.True(File.Exists(P("all", "src", "docs", "deep", "b.txt")));
        Assert.True(Directory.Exists(P("all", "src", "empty")));

        await ArchiveTools.ExtractAsync(archive, [@"src\readme.txt"], P("one"), index, new ScriptedCallbacks());
        Assert.Equal(["readme.txt"], Directory.GetFiles(P("one")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Extract_conflict_skip_keeps_existing_file()
    {
        var archive = P("a.zip");
        await ArchiveTools.PackAsync([MakeSampleTree()], archive, PackFormat.Zip, new ScriptedCallbacks());
        Write(@"t\readme.txt", "mine");
        var cb = new ScriptedCallbacks(ConflictChoice.Skip);
        var r = await ArchiveTools.ExtractAsync(archive, [@"src\readme.txt"], P("t"), new ArchiveProvider().GetIndex(archive), cb);
        Assert.Equal(1, r.Skipped);
        Assert.Equal("mine", File.ReadAllText(P("t", "readme.txt")));
    }

    [Fact]
    public async Task Zip_slip_entries_are_refused()
    {
        var archive = P("evil.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) w.Write("x");
            using (var w = new StreamWriter(zip.CreateEntry("ok.txt").Open())) w.Write("y");
        }
        Directory.CreateDirectory(P("target"));
        var index = new ArchiveProvider().GetIndex(archive);
        var r = await ArchiveTools.ExtractAsync(archive, [""], P("target"), index, new ScriptedCallbacks());
        Assert.False(File.Exists(P("escaped.txt")));
        Assert.True(File.Exists(P("target", "ok.txt")));
        Assert.Equal(1, r.Errors);
    }

    [Fact]
    public void Split_finds_archive_inside_path()
    {
        Write("data.zip", "not really a zip");
        Assert.True(ArchiveProvider.TrySplit(P("data.zip", "a", "b"), out var archive, out var inner));
        Assert.Equal(P("data.zip"), archive);
        Assert.Equal(@"a\b", inner);
        Assert.False(ArchiveProvider.TrySplit(P("folder.zip", "x"), out _, out _)); // does not exist
        Assert.False(ArchiveProvider.TrySplit("sftp://srv/a.zip", out _, out _));
    }

    [Fact]
    public void Normalizes_archive_keys()
    {
        Assert.Equal(@"a\b.txt", ArchiveIndex.NormalizeKey("./a/b.txt"));
        Assert.Equal("dir", ArchiveIndex.NormalizeKey("dir/"));
        Assert.Equal("", ArchiveIndex.NormalizeKey("./"));
    }
}

/// <summary>An in-memory "server" to test remote browsing and transfers without a network.</summary>
internal sealed class FakeRemoteClient : IRemoteClient
{
    public static readonly Dictionary<string, byte[]?> Files = new(StringComparer.Ordinal); // null value = directory

    public bool IsConnected { get; private set; }
    public void Connect(CancellationToken ct = default) => IsConnected = true;
    public void Dispose() => IsConnected = false;

    public IReadOnlyList<FileEntry> List(string path, CancellationToken ct = default)
    {
        var dir = RemotePath.Normalize(path);
        if (dir != "/" && !(Files.TryGetValue(dir, out var d) && d is null)) throw new IOException("No such directory " + dir);
        return Files.Where(kv => RemotePath.Parent(kv.Key) == dir)
            .Select(kv => new FileEntry(RemotePath.Name(kv.Key), kv.Key, kv.Value is null, kv.Value?.Length ?? 0, new DateTime(2025, 1, 1), kv.Value is null ? FileAttributes.Directory : FileAttributes.Normal))
            .ToList();
    }

    public bool DirectoryExists(string path) => RemotePath.Normalize(path) == "/" || (Files.TryGetValue(RemotePath.Normalize(path), out var v) && v is null);

    public (long Size, DateTime Modified)? GetFileInfo(string path) =>
        Files.TryGetValue(RemotePath.Normalize(path), out var v) && v is not null ? (v.Length, new DateTime(2025, 1, 1)) : null;

    public void Download(string path, Stream destination, Action<long> progress, CancellationToken ct)
    {
        var data = Files[RemotePath.Normalize(path)]!;
        destination.Write(data);
        progress(data.Length);
    }

    public void Upload(Stream source, string path, Action<long> progress, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        source.CopyTo(ms);
        Files[RemotePath.Normalize(path)] = ms.ToArray();
        progress(ms.Length);
    }

    public void CreateDirectory(string path) => Files[RemotePath.Normalize(path)] = null;
    public void DeleteFile(string path) => Files.Remove(RemotePath.Normalize(path));

    public void DeleteDirectory(string path)
    {
        var p = RemotePath.Normalize(path);
        foreach (var k in Files.Keys.Where(k => k == p || k.StartsWith(p + "/", StringComparison.Ordinal)).ToList()) Files.Remove(k);
    }

    public void Rename(string from, string to)
    {
        var f = RemotePath.Normalize(from);
        var t = RemotePath.Normalize(to);
        foreach (var k in Files.Keys.Where(k => k == f || k.StartsWith(f + "/", StringComparison.Ordinal)).ToList())
        {
            Files[t + k[f.Length..]] = Files[k];
            Files.Remove(k);
        }
    }
}

[Collection("fake-remote")] // the fake server is static: run these tests one at a time
public class RemoteTests : TempFolderTest
{
    private static (VirtualFileSystem Vfs, RemoteSession Session) Connect()
    {
        FakeRemoteClient.Files.Clear();
        FakeRemoteClient.Files["/pub"] = null;
        FakeRemoteClient.Files["/pub/readme.txt"] = Encoding.UTF8.GetBytes("remote hello");
        FakeRemoteClient.Files["/pub/img"] = null;
        FakeRemoteClient.Files["/pub/img/logo.png"] = [1, 2, 3];
        var vfs = new VirtualFileSystem();
        var session = new RemoteSession(new RemoteConnection { Name = "Test", Host = "example", Protocol = RemoteProtocol.Ftp }, () => new FakeRemoteClient());
        vfs.Sessions.Add(session);
        return (vfs, session);
    }

    [Fact]
    public void Paths_parse_and_normalize()
    {
        Assert.True(RemotePath.TryParsePanelPath("sftp://My Server/home//me/../you", out var scheme, out var name, out var remote));
        Assert.Equal(("sftp", "My Server", "/home/you"), (scheme, name, remote));
        Assert.Equal("/", RemotePath.Normalize(""));
        Assert.Null(RemotePath.Parent("/"));
        Assert.Equal("/a", RemotePath.Parent("/a/b"));
        Assert.False(RemotePath.TryParsePanelPath(@"C:\x", out _, out _, out _));
    }

    [Fact]
    public void Browses_through_the_virtual_file_system()
    {
        var (vfs, _) = Connect();
        Assert.Equal(LocationKind.Remote, vfs.KindOf("ftp://Test/pub"));
        var listing = vfs.Read("ftp://Test/pub", SortOrder.Default, showHidden: false);
        Assert.Equal(["..", "img", "readme.txt"], listing.Select(e => e.Name));
        Assert.Equal("ftp://Test/pub/img", listing[1].FullPath);
        Assert.Equal("ftp://Test/", listing[0].FullPath);
        Assert.Null(vfs.GetParent("ftp://Test/"));
        Assert.Throws<IOException>(() => vfs.Read("ftp://Other/", SortOrder.Default, false));
    }

    [Fact]
    public async Task Download_and_upload_folders()
    {
        var (_, session) = Connect();
        var down = await RemoteTransfer.DownloadAsync(session, ["/pub"], Root, new ScriptedCallbacks());
        Assert.Equal(2, down.FilesDone);
        Assert.Equal("remote hello", File.ReadAllText(P("pub", "readme.txt")));
        Assert.Equal(3, new FileInfo(P("pub", "img", "logo.png")).Length);

        Write(@"up\new.txt", "fresh");
        var up = await RemoteTransfer.UploadAsync(session, [P("up")], "/pub", new ScriptedCallbacks());
        Assert.Equal(1, up.FilesDone);
        Assert.Equal("fresh", Encoding.UTF8.GetString(FakeRemoteClient.Files["/pub/up/new.txt"]!));
    }

    [Fact]
    public async Task Upload_conflict_rename_and_move_deletes_source()
    {
        var (_, session) = Connect();
        var local = Write("readme.txt", "local version");
        await RemoteTransfer.UploadAsync(session, [local], "/pub", new ScriptedCallbacks(ConflictChoice.Rename), deleteSource: true);
        Assert.Equal("remote hello", Encoding.UTF8.GetString(FakeRemoteClient.Files["/pub/readme.txt"]!));
        Assert.Equal("local version", Encoding.UTF8.GetString(FakeRemoteClient.Files["/pub/readme (2).txt"]!));
        Assert.False(File.Exists(local));
    }
}

public class ChecksumTests : TempFolderTest
{
    [Theory]
    [InlineData(ChecksumKind.Md5, "5d41402abc4b2a76b9719d911017c592")]
    [InlineData(ChecksumKind.Sha1, "aaf4c61ddcc5e8a2dabede0f3b482cd9aea9434d")]
    [InlineData(ChecksumKind.Sha256, "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    [InlineData(ChecksumKind.Crc32, "3610a686")]
    public async Task Known_hashes_of_hello(ChecksumKind kind, string expected) =>
        Assert.Equal(expected, await Checksums.ComputeAsync(Write("h.txt", "hello"), kind));

    [Fact]
    public void Parses_gnu_bsd_and_sfv_lines()
    {
        var gnu = Checksums.Parse("# comment\nABCDEF0123456789abcdef0123456789 *dir/a b.txt\n\n", ChecksumKind.Md5);
        Assert.Equal(("dir/a b.txt", "abcdef0123456789abcdef0123456789"), gnu.Single());
        var bsd = Checksums.Parse("SHA256 (x.iso) = AABB", ChecksumKind.Sha256);
        Assert.Equal(("x.iso", "aabb"), bsd.Single());
        var sfv = Checksums.Parse("; generated\nmy file.bin 3610A686", ChecksumKind.Crc32);
        Assert.Equal(("my file.bin", "3610a686"), sfv.Single());
    }

    [Fact]
    public async Task Verify_reports_ok_mismatch_and_missing()
    {
        var a = Write("a.txt", "hello");
        Write("b.txt", "changed");
        var good = await Checksums.ComputeAsync(a, ChecksumKind.Sha256);
        var file = Write("list.sha256",
            Checksums.FormatLine(ChecksumKind.Sha256, good, "a.txt") + "\n" +
            Checksums.FormatLine(ChecksumKind.Sha256, good, "b.txt") + "\n" +
            Checksums.FormatLine(ChecksumKind.Sha256, good, "gone.txt") + "\n");
        var results = await Checksums.VerifyAsync(file);
        Assert.Equal([VerifyState.Ok, VerifyState.Mismatch, VerifyState.Missing], results.Select(r => r.State));
    }
}

using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.Core.Tests;

public class FileEntryTests
{
    private static FileEntry File(string name, long size = 0) =>
        new(name, @"C:\x\" + name, IsDirectory: false, size, new DateTime(2026, 1, 1), FileAttributes.Normal);

    private static FileEntry Dir(string name) =>
        new(name, @"C:\x\" + name, IsDirectory: true, 0, new DateTime(2026, 1, 1), FileAttributes.Directory);

    [Theory]
    [InlineData("report.pdf", "report", "pdf")]
    [InlineData("archive.tar.gz", "archive.tar", "gz")]
    [InlineData(".gitignore", ".gitignore", "")]
    [InlineData("README", "README", "")]
    [InlineData("trailing.", "trailing.", "")]
    public void Splits_name_and_extension(string name, string baseName, string ext)
    {
        var entry = File(name);
        Assert.Equal(baseName, entry.BaseName);
        Assert.Equal(ext, entry.Extension);
    }

    [Fact]
    public void Directories_have_no_extension()
    {
        var entry = Dir("my.folder");
        Assert.Equal("my.folder", entry.BaseName);
        Assert.Equal("", entry.Extension);
    }

    [Fact]
    public void Formats_attributes()
    {
        Assert.Equal("ra-s", AttributeFormatter.Format(FileAttributes.ReadOnly | FileAttributes.Archive | FileAttributes.System));
        Assert.Equal("----", AttributeFormatter.Format(FileAttributes.Normal));
    }

    [Fact]
    public void Sorts_parent_then_folders_then_files_naturally()
    {
        var entries = new List<FileEntry>
        {
            File("file10.txt"), Dir("b"), File("file2.txt"), FileEntry.ParentLink(@"C:\"), Dir("A"), File("file1.txt"),
        };
        entries.Sort(new FileEntryComparer(SortOrder.Default));
        Assert.Equal(["..", "A", "b", "file1.txt", "file2.txt", "file10.txt"], entries.Select(e => e.Name));
    }

    [Fact]
    public void Descending_size_keeps_folders_on_top()
    {
        var entries = new List<FileEntry> { File("small", 1), Dir("dir"), File("big", 100) };
        entries.Sort(new FileEntryComparer(new SortOrder(SortColumn.Size, Descending: true)));
        // Descending flips everything except the folders-first rule.
        Assert.Equal(["dir", "big", "small"], entries.Select(e => e.Name));
    }

    [Fact]
    public void Toggle_flips_same_column_and_resets_on_new_column()
    {
        var order = SortOrder.Default.Toggle(SortColumn.Name);
        Assert.True(order.Descending);
        order = order.Toggle(SortColumn.Size);
        Assert.Equal(new SortOrder(SortColumn.Size), order);
    }
}

public class FormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(10 * 1024 * 1024, "10 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    public void Human_sizes(long bytes, string expected) => Assert.Equal(expected, SizeFormatter.Human(bytes));

    [Fact]
    public void Exact_size_groups_thousands() => Assert.Equal("1 234 567", SizeFormatter.Exact(1234567));
}

public class WildcardMaskTests
{
    [Theory]
    [InlineData("*.*", "anything", true)]
    [InlineData("*.txt", "Notes.TXT", true)]
    [InlineData("*.txt", "notes.md", false)]
    [InlineData("*.txt;*.md", "notes.md", true)]
    [InlineData("report??.doc", "report01.doc", true)]
    [InlineData("report??.doc", "report1.doc", false)]
    [InlineData("*.* | *.bak", "old.bak", false)]
    [InlineData("*.* | *.bak", "new.cs", true)]
    [InlineData("a+b(1).txt", "a+b(1).txt", true)]
    public void Matches(string mask, string name, bool expected) =>
        Assert.Equal(expected, new WildcardMask(mask).IsMatch(name));
}

public class PathHelperTests
{
    [Fact]
    public void Resolves_relative_parent_and_drive()
    {
        Assert.Equal(@"C:\a\b", PathHelper.Resolve(@"C:\a", "b"));
        Assert.Equal(@"C:\", PathHelper.Resolve(@"C:\a", ".."));
        Assert.Equal(@"D:\", PathHelper.Resolve(@"C:\a", "d:"));
        Assert.Equal(@"E:\x", PathHelper.Resolve(@"C:\a", @"E:\x"));
        Assert.Equal(@"C:\a", PathHelper.Resolve(@"C:\a", "   "));
    }

    [Fact]
    public void Splits_local_path_into_clickable_segments()
    {
        var s = PathHelper.Segments(@"D:\Chicken Commander\demo\Photos\2025 Trip");
        Assert.Equal(["D:", "Chicken Commander", "demo", "Photos", "2025 Trip"], s.Select(x => x.Label));
        Assert.Equal([@"D:\", @"D:\Chicken Commander", @"D:\Chicken Commander\demo", @"D:\Chicken Commander\demo\Photos", @"D:\Chicken Commander\demo\Photos\2025 Trip"], s.Select(x => x.Path));
    }

    [Fact]
    public void Segments_of_drive_root_share_archive_and_server()
    {
        Assert.Equal([new PathSegment("C:", @"C:\")], PathHelper.Segments(@"C:\"));
        Assert.Equal([@"\\srv\share", @"\\srv\share\docs"], PathHelper.Segments(@"\\srv\share\docs").Select(x => x.Path));
        Assert.Equal(@"D:\a\b.zip\inner", PathHelper.Segments(@"D:\a\b.zip\inner")[^1].Path);
        var remote = PathHelper.Segments("sftp://myserver/home/user");
        Assert.Equal(["sftp://myserver", "home", "user"], remote.Select(x => x.Label));
        Assert.Equal(["sftp://myserver/", "sftp://myserver/home", "sftp://myserver/home/user"], remote.Select(x => x.Path));
        Assert.Equal(["sftp://myserver/"], PathHelper.Segments("sftp://myserver/").Select(x => x.Path));
        Assert.Empty(PathHelper.Segments(""));
    }
}

public class DirectoryReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "cc-tests-" + Guid.NewGuid().ToString("N"));

    public DirectoryReaderTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        System.IO.File.WriteAllText(Path.Combine(root, "b.txt"), "hello");
        System.IO.File.WriteAllText(Path.Combine(root, "a.txt"), "");
        var hidden = Path.Combine(root, "secret.txt");
        System.IO.File.WriteAllText(hidden, "");
        System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);
    }

    [Fact]
    public void Lists_with_parent_link_and_hides_hidden_files()
    {
        var entries = DirectoryReader.Read(root, SortOrder.Default);
        Assert.Equal(["..", "sub", "a.txt", "b.txt"], entries.Select(e => e.Name));
        Assert.Equal(5, entries.Single(e => e.Name == "b.txt").Size);
    }

    [Fact]
    public void Shows_hidden_files_on_request()
    {
        var entries = DirectoryReader.Read(root, SortOrder.Default, showHidden: true);
        Assert.Contains(entries, e => e.Name == "secret.txt" && e.IsHidden);
    }

    [Fact]
    public void Unique_name_appends_counter()
    {
        Assert.Equal("c.txt", PathHelper.UniqueName(root, "c.txt"));
        Assert.Equal("a (2).txt", PathHelper.UniqueName(root, "a.txt"));
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(root)) System.IO.File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}

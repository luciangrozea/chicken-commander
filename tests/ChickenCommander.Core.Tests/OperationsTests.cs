using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;
using ChickenCommander.Core.Settings;

namespace ChickenCommander.Core.Tests;

/// <summary>Creates a throw-away folder tree per test.</summary>
public abstract class TempFolderTest : IDisposable
{
    protected readonly string Root = Path.Combine(Path.GetTempPath(), "cc-tests-" + Guid.NewGuid().ToString("N"));

    protected TempFolderTest() => Directory.CreateDirectory(Root);

    protected string P(params string[] parts) => Path.Combine([Root, .. parts]);

    protected string Write(string relative, string content = "x", DateTime? modified = null)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (modified is { } m) File.SetLastWriteTime(path, m);
        return path;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var f in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, File.GetAttributes(f) & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class ScriptedCallbacks(params ConflictChoice[] answers) : ITransferCallbacks
{
    private readonly Queue<ConflictChoice> answers = new(answers);
    public int Conflicts { get; private set; }
    public TransferProgress? Last { get; private set; }

    public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict)
    {
        Conflicts++;
        return Task.FromResult(answers.Count > 0 ? answers.Dequeue() : ConflictChoice.Skip);
    }

    public Task<ErrorChoice> HandleErrorAsync(string path, Exception error) => Task.FromResult(ErrorChoice.Skip);

    public void Report(TransferProgress progress) => Last = progress;
}

public class FileTransferTests : TempFolderTest
{
    [Fact]
    public async Task Copies_folder_tree_with_progress()
    {
        Write(@"src\a.txt", "hello");
        Write(@"src\sub\b.txt", "world!");
        Directory.CreateDirectory(P("src", "empty"));
        Directory.CreateDirectory(P("dst"));
        var cb = new ScriptedCallbacks();

        var result = await FileTransfer.RunAsync(new TransferRequest(TransferKind.Copy, [P("src")], P("dst")), cb);

        Assert.Equal(2, result.FilesDone);
        Assert.Equal("world!", File.ReadAllText(P("dst", "src", "sub", "b.txt")));
        Assert.True(Directory.Exists(P("dst", "src", "empty")));
        Assert.True(File.Exists(P("src", "a.txt")));
        Assert.Equal(11, cb.Last!.BytesTotal);
        Assert.Equal(11, cb.Last.BytesDone);
    }

    [Fact]
    public async Task Single_file_target_that_is_not_a_folder_means_new_name()
    {
        Write("a.txt", "data");
        await FileTransfer.RunAsync(new TransferRequest(TransferKind.Copy, [P("a.txt")], P("copy.txt")), new ScriptedCallbacks());
        Assert.Equal("data", File.ReadAllText(P("copy.txt")));
    }

    [Fact]
    public async Task Copy_onto_itself_creates_numbered_copy()
    {
        Write("a.txt");
        await FileTransfer.RunAsync(new TransferRequest(TransferKind.Copy, [P("a.txt")], Root + "\\"), new ScriptedCallbacks());
        Assert.True(File.Exists(P("a (2).txt")));
    }

    [Fact]
    public async Task Move_to_same_name_in_other_case_renames_file()
    {
        Write("readme.txt", "data");
        var result = await FileTransfer.RunAsync(new TransferRequest(TransferKind.Move, [P("readme.txt")], P("README.txt")), new ScriptedCallbacks());
        Assert.Equal(1, result.FilesDone);
        Assert.Equal(["README.txt"], Directory.GetFiles(Root).Select(Path.GetFileName));
        Assert.Equal("data", File.ReadAllText(P("README.txt")));
    }

    [Fact]
    public async Task Move_to_same_name_in_other_case_renames_folder()
    {
        Write(@"photos\a.jpg");
        await FileTransfer.RunAsync(new TransferRequest(TransferKind.Move, [P("photos")], P("Photos")), new ScriptedCallbacks());
        Assert.Equal(["Photos"], Directory.GetDirectories(Root).Select(Path.GetFileName));
        Assert.True(File.Exists(P("Photos", "a.jpg")));
    }

    [Fact]
    public async Task Move_onto_itself_is_skipped()
    {
        Write("a.txt", "data");
        var result = await FileTransfer.RunAsync(new TransferRequest(TransferKind.Move, [P("a.txt")], Root + "\\"), new ScriptedCallbacks());
        Assert.Equal(1, result.Skipped);
        Assert.Equal("data", File.ReadAllText(P("a.txt")));
    }

    [Fact]
    public async Task Refuses_to_copy_folder_into_itself()
    {
        Write(@"src\a.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileTransfer.RunAsync(new TransferRequest(TransferKind.Copy, [P("src")], P("src", "inner") + "\\"), new ScriptedCallbacks()));
    }

    [Fact]
    public async Task Move_on_same_volume_renames_folder()
    {
        Write(@"src\a.txt");
        Directory.CreateDirectory(P("dst"));
        var result = await FileTransfer.RunAsync(new TransferRequest(TransferKind.Move, [P("src")], P("dst")), new ScriptedCallbacks());
        Assert.Equal(1, result.FilesDone);
        Assert.False(Directory.Exists(P("src")));
        Assert.True(File.Exists(P("dst", "src", "a.txt")));
    }

    [Fact]
    public async Task Move_into_existing_folder_merges_and_removes_source()
    {
        Write(@"src\a.txt");
        Write(@"dst\src\other.txt");
        await FileTransfer.RunAsync(new TransferRequest(TransferKind.Move, [P("src")], P("dst")), new ScriptedCallbacks());
        Assert.False(Directory.Exists(P("src")));
        Assert.True(File.Exists(P("dst", "src", "a.txt")));
        Assert.True(File.Exists(P("dst", "src", "other.txt")));
    }

    [Fact]
    public async Task Conflict_choices_skip_overwrite_and_rename()
    {
        Write(@"src\1.txt", "new1");
        Write(@"src\2.txt", "new2");
        Write(@"src\3.txt", "new3");
        Write(@"dst\1.txt", "old1");
        Write(@"dst\2.txt", "old2");
        Write(@"dst\3.txt", "old3");
        var cb = new ScriptedCallbacks(ConflictChoice.Skip, ConflictChoice.Overwrite, ConflictChoice.Rename);

        var result = await FileTransfer.RunAsync(
            new TransferRequest(TransferKind.Copy, [P("src", "1.txt"), P("src", "2.txt"), P("src", "3.txt")], P("dst")), cb);

        Assert.Equal(3, cb.Conflicts);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("old1", File.ReadAllText(P("dst", "1.txt")));
        Assert.Equal("new2", File.ReadAllText(P("dst", "2.txt")));
        Assert.Equal("old3", File.ReadAllText(P("dst", "3.txt")));
        Assert.Equal("new3", File.ReadAllText(P("dst", "3 (2).txt")));
    }

    [Fact]
    public async Task Overwrite_all_stops_asking()
    {
        Write(@"src\1.txt", "n");
        Write(@"src\2.txt", "n");
        Write(@"dst\1.txt", "o");
        Write(@"dst\2.txt", "o");
        var cb = new ScriptedCallbacks(ConflictChoice.OverwriteAll);
        await FileTransfer.RunAsync(new TransferRequest(TransferKind.Copy, [P("src", "1.txt"), P("src", "2.txt")], P("dst")), cb);
        Assert.Equal(1, cb.Conflicts);
        Assert.Equal("n", File.ReadAllText(P("dst", "2.txt")));
    }

    [Fact]
    public async Task Overwrite_older_mode_keeps_newer_targets()
    {
        Write(@"src\old.txt", "src", new DateTime(2020, 1, 1));
        Write(@"src\new.txt", "src", new DateTime(2026, 1, 1));
        Write(@"dst\old.txt", "dst", new DateTime(2025, 1, 1));
        Write(@"dst\new.txt", "dst", new DateTime(2025, 1, 1));
        await FileTransfer.RunAsync(new TransferRequest(TransferKind.Copy, [P("src", "old.txt"), P("src", "new.txt")], P("dst"), OverwriteMode.OverwriteOlder), new ScriptedCallbacks());
        Assert.Equal("dst", File.ReadAllText(P("dst", "old.txt")));
        Assert.Equal("src", File.ReadAllText(P("dst", "new.txt")));
    }

    [Fact]
    public async Task Preserves_timestamp_and_overwrites_read_only_target()
    {
        var stamp = new DateTime(2021, 5, 6, 7, 8, 9);
        Write(@"src\a.txt", "new", stamp);
        var target = Write(@"dst\a.txt", "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        await FileTransfer.RunAsync(new TransferRequest(TransferKind.Copy, [P("src", "a.txt")], P("dst"), OverwriteMode.OverwriteAll), new ScriptedCallbacks());
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal(stamp, File.GetLastWriteTime(target));
    }

    [Fact]
    public async Task Cancel_before_start_copies_nothing()
    {
        Write(@"src\a.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileTransfer.RunAsync(new TransferRequest(TransferKind.Copy, [P("src")], P("dst") + "\\"), new ScriptedCallbacks(), ct: cts.Token));
        Assert.False(File.Exists(P("dst", "src", "a.txt")));
    }

    [Fact]
    public void Maps_destinations()
    {
        Directory.CreateDirectory(P("existing"));
        Assert.Equal(P("existing", "a.txt"), FileTransfer.MapDestinations([P("a.txt")], P("existing"))[0].Destination);
        Assert.Equal(P("new.txt"), FileTransfer.MapDestinations([P("a.txt")], P("new.txt"))[0].Destination);
        Assert.Equal(P("newdir", "a.txt"), FileTransfer.MapDestinations([P("a.txt"), P("b.txt")], P("newdir"))[0].Destination);
    }
}

public class PauseControllerTests
{
    [Fact]
    public async Task Pause_blocks_until_resume()
    {
        var pause = new PauseController();
        pause.Pause();
        var wait = pause.WaitIfPausedAsync(CancellationToken.None);
        await Task.Delay(30);
        Assert.False(wait.IsCompleted);
        pause.Resume();
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(pause.IsPaused);
    }
}

public class MultiRenameTests : TempFolderTest
{
    private static readonly DateTime Stamp = new(2026, 3, 4, 5, 6, 7);

    private static RenameSource S(string path, bool dir = false) => new(path, dir, Stamp);

    [Theory]
    [InlineData("[N]", "[E]", "Holiday Photo.JPG")]
    [InlineData("[N1-3]", "[E]", "Hol.JPG")]
    [InlineData("[N3]", "[E]", "l.JPG")]
    [InlineData("[N9-]", "[E]", "Photo.JPG")]
    [InlineData("[YMD]_[N]", "[E]", "20260304_Holiday Photo.JPG")]
    [InlineData("[P]-[C]", "[E]", "trip-0007.JPG")]
    [InlineData("[N]", "", "Holiday Photo")]
    [InlineData("[Y]-[M]-[D] [h][m][s]", "jpg", "2026-03-04 050607.jpg")]
    public void Masks(string nameMask, string extMask, string expected)
    {
        var o = new RenameOptions { NameMask = nameMask, ExtensionMask = extMask, CounterStart = 5, CounterStep = 2, CounterDigits = 4 };
        Assert.Equal(expected, MultiRename.Apply(o, S(@"C:\trip\Holiday Photo.JPG"), index: 1));
    }

    [Theory]
    [InlineData(CaseMode.Lower, "holiday photo.jpg")]
    [InlineData(CaseMode.Upper, "HOLIDAY PHOTO.JPG")]
    [InlineData(CaseMode.FirstUpper, "Holiday photo.jpg")]
    [InlineData(CaseMode.TitleCase, "Holiday Photo.Jpg")]
    public void Case_conversion(CaseMode mode, string expected) =>
        Assert.Equal(expected, MultiRename.Apply(new RenameOptions { Case = mode }, S(@"C:\x\hOLIDAY pHOTO.jpg"), 0));

    [Fact]
    public void Search_and_replace_plain_and_regex()
    {
        Assert.Equal("my_file_name.txt", MultiRename.Apply(new RenameOptions { Search = " ", Replace = "_" }, S(@"C:\x\my file name.txt"), 0));
        Assert.Equal("IMG-12.jpg", MultiRename.Apply(new RenameOptions { Search = @"^DSC_0*(\d+)", Replace = "IMG-$1", UseRegex = true }, S(@"C:\x\DSC_0012.jpg"), 0));
    }

    [Fact]
    public void Preview_flags_duplicates_invalid_names_and_bad_regex()
    {
        var sources = new[] { S(P("a.txt")), S(P("b.txt")) };
        var dup = MultiRename.Preview(new RenameOptions { NameMask = "same" }, sources);
        Assert.Null(dup[0].Error);
        Assert.Equal("Duplicate name", dup[1].Error);

        var invalid = MultiRename.Preview(new RenameOptions { NameMask = "a:b" }, sources);
        Assert.All(invalid, p => Assert.NotNull(p.Error));

        var regex = MultiRename.Preview(new RenameOptions { Search = "(", UseRegex = true }, sources);
        Assert.All(regex, p => Assert.StartsWith("Invalid regular expression", p.Error));
    }

    [Fact]
    public void Preview_flags_collision_with_file_outside_batch()
    {
        Write("taken.txt");
        var p = MultiRename.Preview(new RenameOptions { NameMask = "taken" }, [S(Write("a.txt"))]);
        Assert.Equal("A file with this name already exists", p[0].Error);
    }

    [Fact]
    public void Execute_swaps_names_and_undo_restores()
    {
        var a = Write("a.txt", "A");
        var b = Write("b.txt", "B");
        var previews = new[]
        {
            new RenamePreview(S(a), "a.txt", "b.txt", null),
            new RenamePreview(S(b), "b.txt", "a.txt", null),
        };

        var done = MultiRename.Execute(previews);
        Assert.Equal("B", File.ReadAllText(a));
        Assert.Equal("A", File.ReadAllText(b));

        MultiRename.Undo(done);
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
        Assert.Equal(2, Directory.GetFiles(Root).Length);
    }

    [Fact]
    public void Counter_numbers_files_in_order()
    {
        var sources = new[] { S(Write("x.txt")), S(Write("y.txt")), S(Write("z.txt")) };
        var previews = MultiRename.Preview(new RenameOptions { NameMask = "file[C]", CounterDigits = 2 }, sources);
        Assert.Equal(["file01.txt", "file02.txt", "file03.txt"], previews.Select(p => p.NewName));
        MultiRename.Execute(previews);
        Assert.True(File.Exists(P("file03.txt")));
    }
}

public class AttributeChangerTests : TempFolderTest
{
    [Fact]
    public void Sets_and_clears_flags_and_date_recursively()
    {
        var file = Write(@"dir\sub\a.txt");
        var date = new DateTime(2019, 9, 9, 9, 9, 0);
        var errors = AttributeChanger.Apply([P("dir")], new AttributeChange(Set: FileAttributes.ReadOnly | FileAttributes.Hidden, Modified: date, IncludeSubfolders: true));
        Assert.Empty(errors);
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.Hidden));
        Assert.Equal(date, File.GetLastWriteTime(file));

        AttributeChanger.Apply([file], new AttributeChange(Clear: FileAttributes.ReadOnly | FileAttributes.Hidden));
        Assert.False(File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public void Combined_state_for_three_state_checkbox()
    {
        Assert.True(AttributeChanger.Combined([FileAttributes.ReadOnly, FileAttributes.ReadOnly], FileAttributes.ReadOnly));
        Assert.Null(AttributeChanger.Combined([FileAttributes.ReadOnly, FileAttributes.Normal], FileAttributes.ReadOnly));
        Assert.False(AttributeChanger.Combined([FileAttributes.Normal], FileAttributes.ReadOnly));
    }
}

public class NavigationHistoryTests
{
    [Fact]
    public void Back_forward_and_recent()
    {
        var h = new NavigationHistory();
        h.Visit(@"C:\a");
        h.Visit(@"C:\b");
        h.Visit(@"C:\c");
        Assert.Equal(@"C:\b", h.GoBack());
        Assert.Equal(@"C:\a", h.GoBack());
        Assert.Null(h.GoBack());
        Assert.Equal(@"C:\b", h.GoForward());
        Assert.Equal([@"C:\b", @"C:\a", @"C:\c"], h.Recent());

        h.Visit(@"C:\d");
        Assert.False(h.CanGoForward);
    }
}

public class SettingsStoreTests : TempFolderTest
{
    [Fact]
    public void Round_trips_and_survives_corrupt_file()
    {
        var store = new SettingsStore(P("cfg", "settings.json"));
        Assert.Empty(store.Load().Hotlist);

        var s = new AppSettings { ShowHidden = true };
        s.Hotlist.Add(new HotlistEntry("Work", @"D:\Work"));
        s.Left.Tabs.AddRange([@"C:\", @"D:\"]);
        s.Left.ActiveTab = 1;
        store.Save(s);

        var loaded = store.Load();
        Assert.True(loaded.ShowHidden);
        Assert.Equal(@"D:\Work", loaded.Hotlist.Single().Path);
        Assert.Equal(1, loaded.Left.ActiveTab);
        Assert.True(double.IsNaN(loaded.Window.Left));

        File.WriteAllText(store.FilePath, "{ not json");
        Assert.NotNull(store.Load());
    }
}

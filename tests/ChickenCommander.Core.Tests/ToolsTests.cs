using System.Text;
using ChickenCommander.Core.Tools;
using ChickenCommander.Core.Viewer;

namespace ChickenCommander.Core.Tests;

public class TextDetectorTests
{
    [Fact]
    public void Detects_boms_utf8_and_latin1()
    {
        Assert.Equal("UTF-8 (BOM)", TextDetector.Describe(TextDetector.DetectEncoding([0xEF, 0xBB, 0xBF, (byte)'a'])));
        Assert.Equal("UTF-16 LE", TextDetector.Describe(TextDetector.DetectEncoding([0xFF, 0xFE, (byte)'a', 0])));
        Assert.Equal("UTF-8", TextDetector.Describe(TextDetector.DetectEncoding(Encoding.UTF8.GetBytes("ăîșț"))));
        Assert.Equal(Encoding.Latin1, TextDetector.DetectEncoding([(byte)'c', 0xE9, (byte)'!']));
    }

    [Fact]
    public void Binary_detection()
    {
        Assert.True(TextDetector.IsBinary([0x4D, 0x5A, 0x00, 0x01]));
        Assert.False(TextDetector.IsBinary(Encoding.UTF8.GetBytes("hello\r\nworld\t!")));
        Assert.False(TextDetector.IsBinary([0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0]));
    }

    [Fact]
    public void Truncated_utf8_sequence_at_sample_end_is_valid()
    {
        var bytes = Encoding.UTF8.GetBytes("aș");
        Assert.True(TextDetector.IsValidUtf8(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.False(TextDetector.IsValidUtf8([0x61, 0xC3, 0x28]));
    }

    [Fact]
    public void Hex_line_format()
    {
        var line = HexFormatter.FormatLine(16, "Hello"u8);
        Assert.StartsWith("00000010  48 65 6C 6C 6F", line);
        Assert.EndsWith("Hello", line);
    }
}

public class FileDocumentTests : TempFolderTest
{
    [Fact]
    public void Indexes_lines_in_utf8_with_crlf()
    {
        var path = Write("a.txt", "first\r\nsecond ș\r\n\r\nlast");
        using var doc = FileDocument.Open(path);
        Assert.Equal(4, doc.LineCount);
        Assert.Equal("second ș", doc.GetLine(1));
        Assert.Equal("", doc.GetLine(2));
        Assert.Equal("last", doc.GetLine(3));
        Assert.Equal(2, doc.FindLine("LAST", 0, matchCase: false) - 1);
        Assert.Equal(-1, doc.FindLine("LAST", 0, matchCase: true));
    }

    [Fact]
    public void Indexes_utf16_and_skips_bom()
    {
        var path = P("u.txt");
        File.WriteAllText(path, "one\ntwo\n", Encoding.Unicode);
        using var doc = FileDocument.Open(path);
        Assert.Equal(2, doc.LineCount);
        Assert.Equal("one", doc.GetLine(0));
        Assert.Equal("two", doc.GetLine(1));
    }

    [Fact]
    public void Hex_rows_and_binary_flag()
    {
        var path = P("b.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 20).Select(i => (byte)i).ToArray());
        using var doc = FileDocument.Open(path);
        Assert.True(doc.IsBinary);
        Assert.Equal(2, doc.HexLineCount);
        Assert.StartsWith("00000010  10 11 12 13", doc.GetHexLine(1));
    }

    [Fact]
    public void Empty_file_has_one_empty_line()
    {
        using var doc = FileDocument.Open(Write("empty.txt", ""));
        Assert.Equal(1, doc.LineCount);
        Assert.Equal("", doc.GetLine(0));
    }
}

public class TextDiffTests
{
    private static string Kinds(IEnumerable<DiffLine> rows) => string.Concat(rows.Select(r => r.Kind switch
    {
        DiffKind.Equal => '=',
        DiffKind.Deleted => '-',
        DiffKind.Inserted => '+',
        _ => '~',
    }));

    [Fact]
    public void Identical_inputs_are_all_equal() =>
        Assert.Equal("===", Kinds(TextDiff.Compare(["a", "b", "c"], ["a", "b", "c"])));

    [Fact]
    public void Insert_delete_and_modify()
    {
        var rows = TextDiff.Compare(["a", "b", "c", "d"], ["a", "x", "c", "d", "e"]);
        Assert.Equal("=~==+", Kinds(rows));
        Assert.Equal(("b", "x"), (rows[1].Left, rows[1].Right));
        Assert.Equal(5, rows[4].RightNumber);
    }

    [Fact]
    public void Pure_deletion_in_the_middle()
    {
        var rows = TextDiff.Compare(["a", "b", "c", "d", "e"], ["a", "e"]);
        Assert.Equal("=---=", Kinds(rows));
        Assert.Equal([2, 3, 4], rows.Where(r => r.Kind == DiffKind.Deleted).Select(r => r.LeftNumber!.Value));
    }

    [Fact]
    public void Ignore_case_and_whitespace_options()
    {
        Assert.Equal("~", Kinds(TextDiff.Compare(["Hello World"], ["hello  world"])));
        Assert.Equal("=", Kinds(TextDiff.Compare(["Hello World"], ["hello  world"], new DiffOptions(IgnoreCase: true, IgnoreWhitespace: true))));
    }

    [Fact]
    public void Empty_sides()
    {
        Assert.Equal("++", Kinds(TextDiff.Compare([], ["a", "b"])));
        Assert.Equal("-", Kinds(TextDiff.Compare(["a"], [])));
    }

    [Fact]
    public void Line_numbers_survive_a_scrambled_diff()
    {
        var left = Enumerable.Range(0, 200).Select(i => $"line {i}").ToList();
        var right = left.Where((_, i) => i % 7 != 0).Select((s, i) => i % 11 == 0 ? s + " changed" : s).ToList();
        var rows = TextDiff.Compare(left, right);
        Assert.Equal(left, rows.Where(r => r.Left is not null).Select(r => r.Left!));
        Assert.Equal(right, rows.Where(r => r.Right is not null).Select(r => r.Right!));
    }
}

public class FileCompareTests : TempFolderTest
{
    [Fact]
    public void First_difference_offset()
    {
        var a = Write("a.bin", "abcdef");
        var b = Write("b.bin", "abcXef");
        var c = Write("c.bin", "abcdef");
        var d = Write("d.bin", "abc");
        Assert.Equal(3, TextDiff.FirstDifference(a, b));
        Assert.Null(TextDiff.FirstDifference(a, c));
        Assert.Equal(3, TextDiff.FirstDifference(a, d));
        Assert.True(TextDiff.FilesEqual(a, c));
    }
}

public class FileSearchTests : TempFolderTest
{
    [Fact]
    public void Finds_by_mask_recursively_in_order()
    {
        Write(@"a\report.txt");
        Write(@"a\b\notes.txt");
        Write(@"a\image.png");
        var hits = FileSearch.Search(new SearchCriteria { StartDirectory = Root, NameMask = "*.txt" }).ToList();
        Assert.Equal(["report.txt", "notes.txt"], hits.Select(h => h.Entry.Name));
    }

    [Fact]
    public void Finds_text_with_line_number_and_skips_binary()
    {
        Write("one.txt", "alpha\nThe Needle is here\nomega");
        Write("two.txt", "nothing");
        File.WriteAllBytes(P("bin.dat"), [0, 1, 2, .. Encoding.ASCII.GetBytes("needle")]);
        var hits = FileSearch.Search(new SearchCriteria { StartDirectory = Root, ContainingText = "needle" }).ToList();
        var hit = Assert.Single(hits);
        Assert.Equal("one.txt", hit.Entry.Name);
        Assert.Equal(2, hit.LineNumber);
        Assert.Empty(FileSearch.Search(new SearchCriteria { StartDirectory = Root, ContainingText = "needle", MatchCase = true }));
    }

    [Fact]
    public void Filters_by_size_date_and_folder_names()
    {
        Write("small.txt", "1", new DateTime(2020, 1, 1));
        Write("big.txt", new string('x', 5000), new DateTime(2026, 1, 1));
        Directory.CreateDirectory(P("big folder"));

        Assert.Equal(["big.txt"], FileSearch.Search(new SearchCriteria { StartDirectory = Root, MinSize = 1000 }).Select(h => h.Entry.Name));
        Assert.Equal(["small.txt"], FileSearch.Search(new SearchCriteria { StartDirectory = Root, ModifiedBefore = new DateTime(2021, 1, 1) }).Select(h => h.Entry.Name));
        Assert.Equal(["big folder", "big.txt"], FileSearch.Search(new SearchCriteria { StartDirectory = Root, NameMask = "big*" }).Select(h => h.Entry.Name));
    }

    [Fact]
    public void Cancellation_stops_the_walk()
    {
        Write(@"x\a.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FileSearch.Search(new SearchCriteria { StartDirectory = Root }, ct: cts.Token).ToList());
    }
}

public class DirectorySyncTests : TempFolderTest
{
    private static readonly DateTime Old = new(2024, 1, 1, 12, 0, 0);
    private static readonly DateTime New = new(2026, 1, 1, 12, 0, 0);

    private IReadOnlyList<SyncItem> Setup(SyncOptions? options = null)
    {
        Write(@"L\same.txt", "same", Old);
        Write(@"R\same.txt", "same", Old);
        Write(@"L\newer-left.txt", "v2", New);
        Write(@"R\newer-left.txt", "v1", Old);
        Write(@"L\newer-right.txt", "v1", Old);
        Write(@"R\newer-right.txt", "v2", New);
        Write(@"L\only-left.txt", "x", Old);
        Write(@"R\sub\only-right.txt", "x", Old);
        Write(@"L\sub\deep.txt", "x", Old);
        Write(@"R\sub\deep.txt", "x", Old);
        Write(@"L\conflict.txt", "aaaa", Old);
        Write(@"R\conflict.txt", "bb", Old);
        Directory.CreateDirectory(P("L", "empty-left-folder"));
        return DirectorySync.Compare(P("L"), P("R"), options ?? new SyncOptions());
    }

    [Fact]
    public void Classifies_files_and_suggests_actions()
    {
        var items = Setup().ToDictionary(i => i.RelativePath);
        Assert.Equal(SyncState.Equal, items["same.txt"].State);
        Assert.Equal(SyncState.Equal, items[@"sub\deep.txt"].State);
        Assert.Equal(SyncAction.CopyToRight, items["newer-left.txt"].Action);
        Assert.Equal(SyncAction.CopyToLeft, items["newer-right.txt"].Action);
        Assert.Equal(SyncAction.CopyToRight, items["only-left.txt"].Action);
        Assert.Equal(SyncAction.CopyToLeft, items[@"sub\only-right.txt"].Action);
        Assert.Equal(SyncState.Different, items["conflict.txt"].State);
        Assert.Equal(SyncAction.None, items["conflict.txt"].Action);
        Assert.True(items["empty-left-folder"].IsDirectory);
    }

    [Fact]
    public void Content_comparison_catches_same_size_and_date_but_different_bytes()
    {
        Write(@"L\x.txt", "abcd", Old);
        Write(@"R\x.txt", "abXd", Old);
        Assert.Equal(SyncState.Equal, DirectorySync.Compare(P("L"), P("R"), new SyncOptions()).Single().State);
        Assert.Equal(SyncState.Different, DirectorySync.Compare(P("L"), P("R"), new SyncOptions { CompareContent = true }).Single().State);
    }

    [Fact]
    public async Task Execute_makes_both_sides_equal()
    {
        var items = Setup();
        var copied = await DirectorySync.ExecuteAsync(P("L"), P("R"), items, new ScriptedCallbacks());
        Assert.Equal(4, copied);
        Assert.Equal("v2", File.ReadAllText(P("R", "newer-left.txt")));
        Assert.Equal("v2", File.ReadAllText(P("L", "newer-right.txt")));
        Assert.True(File.Exists(P("L", "sub", "only-right.txt")));
        Assert.True(Directory.Exists(P("R", "empty-left-folder")));

        var again = DirectorySync.Compare(P("L"), P("R"), new SyncOptions());
        Assert.All(again.Where(i => i.RelativePath != "conflict.txt"), i => Assert.Equal(SyncState.Equal, i.State));
    }
}

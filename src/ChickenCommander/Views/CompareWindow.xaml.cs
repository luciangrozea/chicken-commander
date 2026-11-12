using System.Windows;
using System.Windows.Input;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Tools;
using ChickenCommander.Core.Viewer;

namespace ChickenCommander.Views;

/// <summary>Side-by-side text comparison. F7 / Shift+F7 jump between differences.</summary>
public partial class CompareWindow : Window
{
    private string leftPath;
    private string rightPath;
    private IReadOnlyList<DiffLine> rows = [];

    public CompareWindow(string leftPath, string rightPath)
    {
        InitializeComponent();
        this.leftPath = leftPath;
        this.rightPath = rightPath;
        Loaded += async (_, _) => await CompareAsync();
    }

    /// <summary>
    /// For binary files a line diff is meaningless: returns a message with the first differing byte instead,
    /// or null when the files are text and the window should be shown.
    /// </summary>
    public static string? BinarySummary(string left, string right)
    {
        if (!IsBinary(left) && !IsBinary(right)) return null;
        var offset = TextDiff.FirstDifference(left, right);
        return offset is null
            ? "The files are identical."
            : $"The files differ, first at byte offset {offset:N0} (0x{offset:X}).\n\n" +
              $"{Path.GetFileName(left)}: {SizeFormatter.Exact(new FileInfo(left).Length)} bytes\n" +
              $"{Path.GetFileName(right)}: {SizeFormatter.Exact(new FileInfo(right).Length)} bytes";
    }

    private static bool IsBinary(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[Math.Min(8192, s.Length)];
        s.ReadExactly(head);
        return TextDetector.IsBinary(head);
    }

    private async Task CompareAsync()
    {
        LeftTitle.Text = leftPath;
        RightTitle.Text = rightPath;
        SummaryText.Text = "Comparing…";
        var options = new DiffOptions(IgnoreCaseBox.IsChecked == true, IgnoreSpaceBox.IsChecked == true);
        var (l, r) = (leftPath, rightPath);
        try
        {
            rows = await Task.Run(() => TextDiff.Compare(ReadLines(l), ReadLines(r), options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = ex.Message;
            return;
        }
        Rows.ItemsSource = rows;

        var blocks = CountBlocks();
        SummaryText.Text = blocks == 0
            ? "The files are identical" + (options.IgnoreCase || options.IgnoreWhitespace ? " (with the chosen options)." : ".")
            : $"{blocks} difference(s) · {rows.Count(x => x.Kind == DiffKind.Modified)} changed, " +
              $"{rows.Count(x => x.Kind == DiffKind.Deleted)} only left, {rows.Count(x => x.Kind == DiffKind.Inserted)} only right";
        if (blocks > 0) JumpToDifference(forward: true, fromIndex: -1);
    }

    private static List<string> ReadLines(string path)
    {
        using var doc = FileDocument.Open(path);
        var lines = new List<string>(doc.LineCount);
        for (var i = 0; i < doc.LineCount; i++) lines.Add(doc.GetLine(i));
        return lines;
    }

    private int CountBlocks()
    {
        var blocks = 0;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Kind != DiffKind.Equal && (i == 0 || rows[i - 1].Kind == DiffKind.Equal)) blocks++;
        return blocks;
    }

    /// <summary>Moves to the start of the next (or previous) block of differences.</summary>
    private void JumpToDifference(bool forward, int? fromIndex = null)
    {
        var start = fromIndex ?? Rows.SelectedIndex;
        var step = forward ? 1 : -1;
        var i = start + step;
        // Skip the rest of the block we are in.
        while (i >= 0 && i < rows.Count && rows[i].Kind != DiffKind.Equal && start >= 0 && rows[start].Kind != DiffKind.Equal) i += step;
        while (i >= 0 && i < rows.Count && rows[i].Kind == DiffKind.Equal) i += step;
        if (i < 0 || i >= rows.Count) return;
        if (!forward)
            while (i > 0 && rows[i - 1].Kind != DiffKind.Equal) i--; // go to the block's first line

        Rows.SelectedIndex = i;
        Rows.ScrollIntoView(rows[Math.Min(rows.Count - 1, i + 8)]); // a little context below
        Rows.ScrollIntoView(rows[i]);
    }

    private async void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) await CompareAsync();
    }

    private void Next_Click(object sender, RoutedEventArgs e) => JumpToDifference(true);

    private void Previous_Click(object sender, RoutedEventArgs e) => JumpToDifference(false);

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        (leftPath, rightPath) = (rightPath, leftPath);
        await CompareAsync();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F7:
                JumpToDifference(forward: Keyboard.Modifiers != ModifierKeys.Shift);
                e.Handled = true;
                break;
        }
    }
}

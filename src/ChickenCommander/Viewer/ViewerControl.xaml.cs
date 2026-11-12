using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Viewer;

namespace ChickenCommander.Viewer;

public enum ViewerMode { Auto, Text, Hex, Image }

/// <summary>
/// Shows a file as text, hex or image. Used by the Lister window (F3) and by the Quick View panel (Ctrl+Q).
/// Files are indexed on a background thread; switching files cancels the previous load.
/// </summary>
public partial class ViewerControl : UserControl
{
    /// <summary>Up to this size, text is shown in a normal text box (selectable, wrappable).</summary>
    private const long SmallTextLimit = 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "bmp", "ico", "tif", "tiff", "webp", "jfif",
    };

    private FileDocument? document;
    private CancellationTokenSource? loading;

    public ViewerControl()
    {
        InitializeComponent();
        Unloaded += (_, _) => Close();
    }

    public string? FilePath { get; private set; }
    public ViewerMode ActualMode { get; private set; }

    /// <summary>Encoding, size, line count… for a status bar.</summary>
    public string Info { get; private set; } = string.Empty;

    public event Action? InfoChanged;

    public bool WordWrap
    {
        get => TextView.TextWrapping == TextWrapping.Wrap;
        set
        {
            TextView.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            TextView.HorizontalScrollBarVisibility = value ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }
    }

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).TrimStart('.'));

    public async Task LoadAsync(string path, ViewerMode mode = ViewerMode.Auto)
    {
        loading?.Cancel();
        var cts = loading = new CancellationTokenSource();
        Close(keepCancellation: true);
        FilePath = path;

        if (mode == ViewerMode.Image || (mode == ViewerMode.Auto && IsImage(path)))
        {
            if (await TryShowImageAsync(path, cts.Token)) return;
            if (cts.IsCancellationRequested) return;
            mode = ViewerMode.Auto; // not decodable: fall back to text/hex
        }

        ShowMessage("Loading…");
        FileDocument doc;
        try
        {
            doc = await Task.Run(() => FileDocument.Open(path, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage($"Cannot open the file:\n{ex.Message}");
            return;
        }
        if (cts.IsCancellationRequested)
        {
            doc.Dispose();
            return;
        }

        document = doc;
        var hex = mode == ViewerMode.Hex || (mode == ViewerMode.Auto && doc.IsBinary);
        ActualMode = hex ? ViewerMode.Hex : ViewerMode.Text;
        HideAll();

        if (!hex && doc.Length <= SmallTextLimit)
        {
            TextView.Text = await Task.Run(doc.ReadAllText, cts.Token);
            TextView.Visibility = Visibility.Visible;
            TextView.ScrollToHome();
        }
        else
        {
            LinesView.ItemsSource = new VirtualLines(doc, hex);
            LinesView.Visibility = Visibility.Visible;
        }

        SetInfo(hex
            ? $"Hex · {SizeFormatter.Exact(doc.Length)} bytes"
            : $"Text · {TextDetector.Describe(doc.Encoding)} · {doc.LineCount:N0} lines · {SizeFormatter.Human(doc.Length)}");
    }

    /// <summary>Shows a short message instead of file content (folders, empty selection, errors).</summary>
    public void ShowMessage(string message)
    {
        HideAll();
        MessageView.Text = message;
        MessageView.Visibility = Visibility.Visible;
        SetInfo(string.Empty);
    }

    /// <summary>Selects the next match after the current position (wrapping around). Returns false if not found.</summary>
    public bool FindNext(string text, bool matchCase)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (TextView.Visibility == Visibility.Visible)
        {
            var content = TextView.Text;
            var start = TextView.SelectionStart + TextView.SelectionLength;
            var index = content.IndexOf(text, Math.Min(start, content.Length), comparison);
            if (index < 0) index = content.IndexOf(text, comparison);
            if (index < 0) return false;
            TextView.Focus();
            TextView.Select(index, text.Length);
            TextView.ScrollToLine(TextView.GetLineIndexFromCharacterIndex(index));
            return true;
        }

        if (LinesView.Visibility == Visibility.Visible && document is not null && ActualMode == ViewerMode.Text)
        {
            var from = LinesView.SelectedIndex + 1;
            var line = document.FindLine(text, Math.Max(0, from), matchCase);
            if (line < 0) return false;
            var item = LinesView.Items[line];
            LinesView.SelectedItem = item;
            LinesView.ScrollIntoView(item);
            return true;
        }

        if (LinesView.Visibility == Visibility.Visible && document is not null && ActualMode == ViewerMode.Hex)
        {
            // Hex view: search the ASCII column so "MZ" or "PNG" can be found.
            var count = LinesView.Items.Count;
            for (var n = 1; n <= count; n++)
            {
                var i = (LinesView.SelectedIndex + n + count) % count;
                if (((ViewerLine)LinesView.Items[i]!).Text[59..].Contains(text, comparison))
                {
                    LinesView.SelectedIndex = i;
                    LinesView.ScrollIntoView(LinesView.Items[i]);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Copies the selected text or lines.</summary>
    public void CopySelection()
    {
        if (TextView.Visibility == Visibility.Visible && TextView.SelectionLength > 0)
        {
            Clipboard.SetText(TextView.SelectedText);
        }
        else if (LinesView.SelectedItems.Count > 0)
        {
            var lines = LinesView.SelectedItems.Cast<ViewerLine>().OrderBy(l => l.Index).Select(l => l.Text);
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
    }

    public void FocusContent()
    {
        if (TextView.Visibility == Visibility.Visible) TextView.Focus();
        else if (LinesView.Visibility == Visibility.Visible) LinesView.Focus();
        else if (ImageView.Visibility == Visibility.Visible) ImageView.Focus();
    }

    public void Close() => Close(keepCancellation: false);

    private void Close(bool keepCancellation)
    {
        if (!keepCancellation) loading?.Cancel();
        LinesView.ItemsSource = null;
        TextView.Clear();
        ImageControl.Source = null;
        document?.Dispose();
        document = null;
    }

    private async Task<bool> TryShowImageAsync(string path, CancellationToken ct)
    {
        try
        {
            var image = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // read fully, then release the file
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }, ct);
            if (ct.IsCancellationRequested) return false;

            HideAll();
            ImageControl.Source = image;
            ImageView.Visibility = Visibility.Visible;
            ActualMode = ViewerMode.Image;
            SetInfo($"Image · {image.PixelWidth} × {image.PixelHeight} px · {SizeFormatter.Human(new FileInfo(path).Length)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException or FileFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    private void HideAll()
    {
        TextView.Visibility = LinesView.Visibility = ImageView.Visibility = MessageView.Visibility = Visibility.Collapsed;
    }

    private void SetInfo(string info)
    {
        Info = info;
        InfoChanged?.Invoke();
    }

    private void LinesView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelection();
            e.Handled = true;
        }
    }

    /// <summary>Text shown for a folder in Quick View.</summary>
    public static string DescribeFolder(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            var options = new EnumerationOptions { IgnoreInaccessible = true };
            var files = dir.EnumerateFiles("*", options).Take(10_001).ToList();
            var folders = dir.EnumerateDirectories("*", options).Take(10_001).Count();
            var sb = new StringBuilder();
            sb.AppendLine($"📁 {dir.Name}");
            sb.AppendLine();
            sb.AppendLine($"{(folders > 10_000 ? "10 000+" : folders.ToString("N0"))} folder(s)");
            sb.AppendLine($"{(files.Count > 10_000 ? "10 000+" : files.Count.ToString("N0"))} file(s), {SizeFormatter.Human(files.Sum(f => f.Length))} (not counting subfolders)");
            sb.AppendLine($"Modified {dir.LastWriteTime:dd.MM.yyyy HH:mm}");
            return sb.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}

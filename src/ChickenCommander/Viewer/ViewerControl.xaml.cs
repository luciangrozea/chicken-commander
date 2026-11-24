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
    private CancellationTokenSource? searching;

    /// <summary>
    /// Images are decoded at most this many pixels wide or high. A 20000 x 15000 photo would otherwise need
    /// 1.2 GB of memory, and the graphics card cannot draw anything that big anyway.
    /// </summary>
    public int MaxImageSide { get; set; } = 4096;

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
        FileDocument? doc = null;
        try
        {
            // Hex mode needs no line index, so a binary file of any size opens at once.
            doc = await Task.Run(() => FileDocument.Open(path, indexLines: false, cts.Token), cts.Token);
            var opened = doc;
            var hex = mode == ViewerMode.Hex || (mode == ViewerMode.Auto && opened.IsBinary);
            if (!hex)
            {
                if (opened.Length > SmallTextLimit) ShowMessage($"Counting lines… ({SizeFormatter.Human(opened.Length)})");
                await Task.Run(() => opened.EnsureIndexed(cts.Token), cts.Token);
            }
            var smallText = !hex && opened.Length <= SmallTextLimit ? await Task.Run(opened.ReadAllText, cts.Token) : null;
            if (cts.IsCancellationRequested)
            {
                opened.Dispose();
                return;
            }

            document = opened;
            ActualMode = hex ? ViewerMode.Hex : ViewerMode.Text;
            HideAll();
            if (smallText is not null)
            {
                TextView.Text = smallText;
                TextView.Visibility = Visibility.Visible;
                TextView.ScrollToHome();
            }
            else
            {
                LinesView.ItemsSource = new VirtualLines(opened, hex);
                LinesView.Visibility = Visibility.Visible;
            }

            SetInfo(hex
                ? $"Hex · {SizeFormatter.Exact(opened.Length)} bytes"
                : $"Text · {TextDetector.Describe(opened.Encoding)} · {opened.LineCount:N0} lines{(opened.IsTruncated ? " (cut off)" : "")} · {SizeFormatter.Human(opened.Length)}");
        }
        catch (OperationCanceledException)
        {
            if (doc != document) doc?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException or ObjectDisposedException)
        {
            if (doc != document) doc?.Dispose();
            if (!cts.IsCancellationRequested) ShowMessage($"Cannot open the file:\n{ex.Message}");
        }
    }

    /// <summary>Shows a short message instead of file content (folders, empty selection, errors).</summary>
    public void ShowMessage(string message)
    {
        HideAll();
        MessageView.Text = message;
        MessageView.Visibility = Visibility.Visible;
        SetInfo(string.Empty);
    }

    /// <summary>True while a search runs in the background (big files take a while).</summary>
    public bool IsSearching => searching is not null;

    /// <summary>Stops a running search.</summary>
    public void CancelSearch() => searching?.Cancel();

    /// <summary>
    /// Selects the next match after the current position (wrapping around). Returns false if not found or cancelled.
    /// Big files are searched on a background thread, so the window stays responsive.
    /// </summary>
    public async Task<bool> FindNextAsync(string text, bool matchCase)
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

        if (LinesView.Visibility != Visibility.Visible || document is not { } doc) return false;
        searching?.Cancel();
        var cts = searching = new CancellationTokenSource();
        var selected = Math.Max(-1, LinesView.SelectedIndex);
        try
        {
            int row;
            if (ActualMode == ViewerMode.Text)
            {
                row = await Task.Run(() => doc.FindLine(text, selected + 1, matchCase, cts.Token), cts.Token);
            }
            else
            {
                // Hex view: search the raw bytes (the ASCII column), so "MZ" or "PNG" can be found.
                var start = (selected + 1L) * FileDocument.HexBytesPerLine;
                var offset = await Task.Run(() => doc.FindBytes(text, start, matchCase, cts.Token), cts.Token);
                row = offset < 0 || offset / FileDocument.HexBytesPerLine > int.MaxValue ? -1 : (int)(offset / FileDocument.HexBytesPerLine);
            }
            if (row < 0 || cts.IsCancellationRequested || document != doc) return false;
            var item = LinesView.Items[row];
            LinesView.SelectedItem = item;
            LinesView.ScrollIntoView(item);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
        {
            return false;
        }
        finally
        {
            if (searching == cts) searching = null;
            cts.Dispose();
        }
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
        searching?.Cancel();
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
            var maxSide = MaxImageSide;
            var (image, width, height) = await Task.Run(() => DecodeImage(path, maxSide), ct);
            if (ct.IsCancellationRequested) return false;

            HideAll();
            ActualMode = ViewerMode.Image;
            var size = SizeFormatter.Human(new FileInfo(path).Length);
            if (image is null)
            {
                MessageView.Text = $"This image is too large to display ({width:N0} × {height:N0} pixels).\nThe hex view (2 in the F3 viewer) still works.";
                MessageView.Visibility = Visibility.Visible;
                SetInfo($"Image · {width:N0} × {height:N0} px · {size}");
                return true;
            }
            ImageControl.Source = image;
            ImageView.Visibility = Visibility.Visible;
            var scaled = image.PixelWidth < width ? $" (shown at {image.PixelWidth:N0} × {image.PixelHeight:N0})" : "";
            SetInfo($"Image · {width:N0} × {height:N0} px{scaled} · {size}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or OperationCanceledException
                                       or FileFormatException or InvalidOperationException or OutOfMemoryException or ArgumentException
                                       or System.Runtime.InteropServices.COMException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the image size first, then decodes it scaled down to at most <paramref name="maxSide"/> pixels per side.
    /// Returns a null image (with the real size) when the decoder cannot handle it at all.
    /// </summary>
    private static (BitmapSource? Image, int Width, int Height) DecodeImage(string path, int maxSide)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;

        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // read fully, then release the file
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (width > maxSide || height > maxSide)
        {
            // Setting one side keeps the aspect ratio.
            if (width >= height) bitmap.DecodePixelWidth = maxSide;
            else bitmap.DecodePixelHeight = maxSide;
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        // WIC gives up on some gigantic images and returns an empty 1 x 1 picture instead of failing.
        if (bitmap.PixelWidth <= 1 && bitmap.PixelHeight <= 1 && width * (long)height > 1) return (null, width, height);
        return (bitmap, width, height);
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

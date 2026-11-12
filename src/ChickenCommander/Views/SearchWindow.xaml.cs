using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Tools;

namespace ChickenCommander.Views;

/// <summary>A search result row.</summary>
public sealed record SearchResultRow(FileEntry Entry, int? LineNumber, string? Line)
{
    public string Folder => Path.GetDirectoryName(Entry.FullPath) ?? string.Empty;
    public string SizeText => Entry.IsDirectory ? "<DIR>" : SizeFormatter.Exact(Entry.Size);
    public string MatchText => LineNumber is { } n ? $"{n}: {Line?.Trim()}" : string.Empty;
}

/// <summary>Alt+F7. Non-modal: results stay available while you work in the panels.</summary>
public partial class SearchWindow : Window
{
    private readonly Action<string> goToFile;
    private readonly Action<IReadOnlyList<string>, int> viewFiles;
    private readonly ObservableCollection<SearchResultRow> results = [];
    private CancellationTokenSource? search;

    public SearchWindow(string startFolder, Action<string> goToFile, Action<IReadOnlyList<string>, int> viewFiles)
    {
        InitializeComponent();
        this.goToFile = goToFile;
        this.viewFiles = viewFiles;
        FolderBox.Text = startFolder;
        Results.ItemsSource = results;
        Loaded += (_, _) =>
        {
            MaskBox.Focus();
            MaskBox.SelectAll();
        };
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(FolderBox.Text.Trim()))
        {
            MessageBox.Show(this, "The folder to search in does not exist.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TryParseKb(MinSizeBox.Text, out var min) || !TryParseKb(MaxSizeBox.Text, out var max))
        {
            MessageBox.Show(this, "Sizes must be numbers (in KB).", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var criteria = new SearchCriteria
        {
            StartDirectory = FolderBox.Text.Trim(),
            NameMask = string.IsNullOrWhiteSpace(MaskBox.Text) ? "*.*" : MaskBox.Text,
            Recursive = SubfoldersBox.IsChecked == true,
            IncludeHidden = HiddenBox.IsChecked == true,
            ContainingText = string.IsNullOrEmpty(ContainsBox.Text) ? null : ContainsBox.Text,
            MatchCase = MatchCaseBox.IsChecked == true,
            MinSize = min,
            MaxSize = max,
            ModifiedAfter = AfterBox.SelectedDate,
            ModifiedBefore = BeforeBox.SelectedDate?.AddDays(1), // "before" includes the whole chosen day
        };

        search?.Cancel();
        var cts = search = new CancellationTokenSource();
        results.Clear();
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        // The walk runs on a worker thread; hits and the current folder are handed over in batches.
        var pending = new List<SearchResultRow>();
        var currentDir = string.Empty;
        var gate = new object();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => Flush();
        timer.Start();

        var cancelled = false;
        try
        {
            await Task.Run(() =>
            {
                foreach (var hit in FileSearch.Search(criteria, d => currentDir = d, cts.Token))
                {
                    lock (gate) pending.Add(new SearchResultRow(hit.Entry, hit.LineNumber, hit.Line));
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            timer.Stop();
            Flush();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
        StatusText.Text = $"{(cancelled ? "Stopped" : "Done")}: {results.Count:N0} item(s) found";
        if (results.Count > 0 && !cancelled)
        {
            Results.SelectedIndex = 0;
            Results.Focus();
        }

        void Flush()
        {
            List<SearchResultRow> batch;
            lock (gate)
            {
                batch = [.. pending];
                pending.Clear();
            }
            foreach (var row in batch) results.Add(row);
            StatusText.Text = $"Searching {currentDir} … {results.Count:N0} found";
        }
    }

    private static bool TryParseKb(string text, out long? bytes)
    {
        bytes = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var kb) || kb < 0) return false;
        bytes = (long)(kb * 1024);
        return true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => search?.Cancel();

    private void GoTo_Click(object sender, RoutedEventArgs e) => GoToSelected();

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => GoToSelected();

    private void GoToSelected()
    {
        if (Results.SelectedItem is SearchResultRow row) goToFile(row.Entry.FullPath);
    }

    private void View_Click(object sender, RoutedEventArgs e) => ViewSelected();

    private void ViewSelected()
    {
        if (Results.SelectedItem is not SearchResultRow row || row.Entry.IsDirectory) return;
        var files = results.Where(r => !r.Entry.IsDirectory).Select(r => r.Entry.FullPath).ToList();
        viewFiles(files, files.IndexOf(row.Entry.FullPath));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Results.IsKeyboardFocusWithin) return;
        if (e.Key == Key.Enter)
        {
            GoToSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            ViewSelected();
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        search?.Cancel();
        base.OnClosed(e);
    }
}

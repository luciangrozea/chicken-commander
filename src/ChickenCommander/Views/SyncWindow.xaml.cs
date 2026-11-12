using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;
using ChickenCommander.Core.Tools;

namespace ChickenCommander.Views;

/// <summary>A row of the sync list; the action can be changed by the user.</summary>
public sealed class SyncRow(SyncItem item) : INotifyPropertyChanged
{
    private static readonly Brush ToRight = new SolidColorBrush(Color.FromRgb(0x2F, 0x5A, 0xA8));
    private static readonly Brush ToLeft = new SolidColorBrush(Color.FromRgb(0xC2, 0x6A, 0x00));
    private static readonly Brush Neutral = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0xA0));

    public SyncItem Item { get; } = item;

    public string LeftName => Item.Left is null ? string.Empty : Item.RelativePath + (Item.IsDirectory ? "\\" : "");
    public string RightName => Item.Right is null ? string.Empty : Item.RelativePath + (Item.IsDirectory ? "\\" : "");
    public string LeftSize => Size(Item.Left);
    public string RightSize => Size(Item.Right);
    public string LeftDate => Item.Left?.LastWriteTime.ToString("dd.MM.yyyy HH:mm") ?? string.Empty;
    public string RightDate => Item.Right?.LastWriteTime.ToString("dd.MM.yyyy HH:mm") ?? string.Empty;

    public string ActionSymbol => Item.Action switch
    {
        SyncAction.CopyToRight => "→",
        SyncAction.CopyToLeft => "←",
        _ => Item.State == SyncState.Equal ? "=" : "≠",
    };

    public Brush ActionBrush => Item.Action switch
    {
        SyncAction.CopyToRight => ToRight,
        SyncAction.CopyToLeft => ToLeft,
        _ => Neutral,
    };

    /// <summary>Cycles → ← nothing, skipping directions that make no sense (no source on that side).</summary>
    public void CycleAction()
    {
        SyncAction[] order = [SyncAction.CopyToRight, SyncAction.CopyToLeft, SyncAction.None];
        var i = Array.IndexOf(order, Item.Action);
        for (var n = 1; n <= 3; n++)
        {
            var next = order[(i + n) % 3];
            if (next == SyncAction.CopyToRight && Item.Left is null) continue;
            if (next == SyncAction.CopyToLeft && Item.Right is null) continue;
            if (next != SyncAction.None && Item.Left is DirectoryInfo != Item.Right is DirectoryInfo && Item.Left is not null && Item.Right is not null) continue;
            Item.Action = next;
            break;
        }
        Changed(nameof(ActionSymbol));
        Changed(nameof(ActionBrush));
    }

    private static string Size(FileSystemInfo? info) => info switch
    {
        FileInfo f => SizeFormatter.Exact(f.Length),
        DirectoryInfo => "<DIR>",
        _ => string.Empty,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class SyncWindow : Window, ITransferCallbacks
{
    private List<SyncRow> all = [];
    private CancellationTokenSource? running;
    private string comparedLeft = string.Empty;
    private string comparedRight = string.Empty;
    private volatile string progressFile = string.Empty;

    public SyncWindow(string left, string right)
    {
        InitializeComponent();
        LeftBox.Text = left;
        RightBox.Text = right;
    }

    /// <summary>True once anything was copied, so the panels can refresh.</summary>
    public bool Changed { get; private set; }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        var left = LeftBox.Text.Trim();
        var right = RightBox.Text.Trim();
        if (!Directory.Exists(left) || !Directory.Exists(right))
        {
            MessageBox.Show(this, "Both folders must exist.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var options = new SyncOptions
        {
            Mask = string.IsNullOrWhiteSpace(MaskBox.Text) ? "*.*" : MaskBox.Text,
            Recursive = SubfoldersBox.IsChecked == true,
            CompareContent = ContentBox.IsChecked == true,
            IgnoreDate = IgnoreDateBox.IsChecked == true,
        };

        SetBusy(true, "Comparing…");
        try
        {
            var items = await Task.Run(() => DirectorySync.Compare(left, right, options));
            all = items.Select(i => new SyncRow(i)).ToList();
            comparedLeft = left;
            comparedRight = right;
            ApplyFilter();
            UpdateSummary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private void ApplyFilter()
    {
        Rows.ItemsSource = all.Where(r => r.Item.State switch
        {
            SyncState.Equal => ShowEqual.IsChecked == true,
            SyncState.LeftNewer or SyncState.LeftOnly => ShowLeft.IsChecked == true,
            SyncState.RightNewer or SyncState.RightOnly => ShowRight.IsChecked == true,
            _ => ShowDifferent.IsChecked == true,
        }).ToList();
    }

    private void UpdateSummary()
    {
        int toRight = all.Count(r => r.Item.Action == SyncAction.CopyToRight);
        int toLeft = all.Count(r => r.Item.Action == SyncAction.CopyToLeft);
        int equal = all.Count(r => r.Item.State == SyncState.Equal);
        int unsure = all.Count(r => r.Item.State == SyncState.Different);
        StatusText.Text = $"{all.Count} item(s): {toRight} → right, {toLeft} ← left, {equal} equal, {unsure} unsure (≠, not copied unless you choose)";
        SyncButton.IsEnabled = toRight + toLeft > 0;
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SyncRow row })
        {
            row.CycleAction();
            UpdateSummary();
        }
    }

    private void Rows_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        foreach (var row in Rows.SelectedItems.Cast<SyncRow>()) row.CycleAction();
        UpdateSummary();
        e.Handled = true;
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        var work = all.Where(r => r.Item.Action != SyncAction.None).Select(r => r.Item).ToList();
        var toRight = work.Count(i => i.Action == SyncAction.CopyToRight);
        var toLeft = work.Count - toRight;
        var question = $"Copy {toRight} item(s) to the right and {toLeft} item(s) to the left?\nExisting older files will be overwritten.";
        if (MessageBox.Show(this, question, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        running = new CancellationTokenSource();
        SetBusy(true, "Synchronizing…");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => StatusText.Text = $"Copying {progressFile}";
        timer.Start();
        try
        {
            var copied = await Task.Run(() => DirectorySync.ExecuteAsync(comparedLeft, comparedRight, work, this, running.Token));
            Changed = true;
            timer.Stop();
            MessageBox.Show(this, $"Done: {copied} file(s) copied.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            Compare_Click(sender, e); // refresh the list to show the new state
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            timer.Stop();
            SetBusy(false, null);
        }
    }

    private void SetBusy(bool busy, string? status)
    {
        CompareButton.IsEnabled = SyncButton.IsEnabled = !busy;
        Rows.IsEnabled = !busy;
        if (status is not null) StatusText.Text = status;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        running?.Cancel();
        base.OnClosing(e);
    }

    // ---- ITransferCallbacks: sync always overwrites; errors are asked on the UI thread ----------

    void ITransferCallbacks.Report(TransferProgress progress) => progressFile = progress.CurrentFile;

    Task<ConflictChoice> ITransferCallbacks.ResolveConflictAsync(ConflictInfo conflict) => Task.FromResult(ConflictChoice.Overwrite);

    Task<ErrorChoice> ITransferCallbacks.HandleErrorAsync(string path, Exception error) =>
        Dispatcher.InvokeAsync(() =>
        {
            var answer = MessageBox.Show(this, $"{path}\n\n{error.Message}\n\nSkip this file and continue?", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes ? ErrorChoice.Skip : ErrorChoice.Cancel;
        }).Task;
}

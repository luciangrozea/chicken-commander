using System.Windows;
using ChickenCommander.Core.Tools;

namespace ChickenCommander.Views;

public partial class VerifyResultsWindow : Window
{
    public VerifyResultsWindow(string checksumFile, IReadOnlyList<VerifyResult> results)
    {
        InitializeComponent();
        Title = $"Verify checksums – {Path.GetFileName(checksumFile)}";
        Rows.ItemsSource = results.Select(r => new
        {
            r.State,
            r.FileName,
            r.Expected,
            Detail = r.State switch
            {
                VerifyState.Ok => "✓",
                VerifyState.Missing => "file not found",
                VerifyState.Error => r.Message,
                _ => r.Actual,
            },
        }).ToList();

        var ok = results.Count(r => r.State == VerifyState.Ok);
        var bad = results.Count(r => r.State == VerifyState.Mismatch);
        var missing = results.Count(r => r.State is VerifyState.Missing or VerifyState.Error);
        SummaryText.Text = results.Count == 0
            ? "No entries found in this checksum file."
            : bad == 0 && missing == 0
                ? $"✓ All {ok} file(s) are OK."
                : $"{ok} OK, {bad} DIFFERENT, {missing} missing or unreadable.";
        SummaryText.Foreground = bad + missing == 0 ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.Firebrick;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

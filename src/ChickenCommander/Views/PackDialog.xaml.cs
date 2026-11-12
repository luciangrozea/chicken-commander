using System.Windows;
using ChickenCommander.Core.Vfs;

namespace ChickenCommander.Views;

public partial class PackDialog : Window
{
    public PackDialog(string description, string defaultPath)
    {
        InitializeComponent();
        MessageText.Text = $"Pack {description} into:";
        PathBox.Text = defaultPath;
        Loaded += (_, _) =>
        {
            PathBox.Focus();
            SelectStem();
        };
    }

    public string ArchivePath => PathBox.Text.Trim();

    public PackFormat Format => TarBox.IsChecked == true ? PackFormat.Tar : TgzBox.IsChecked == true ? PackFormat.TarGz : PackFormat.Zip;

    /// <summary>Switching the format swaps the extension in the file name.</summary>
    private void Format_Checked(object sender, RoutedEventArgs e)
    {
        if (PathBox is null || TarBox is null || TgzBox is null) return; // still initializing
        var text = PathBox.Text;
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".tar", ".zip" })
        {
            if (text.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^ext.Length];
                break;
            }
        }
        PathBox.Text = text + ArchiveTools.DefaultExtension(Format);
    }

    private void SelectStem()
    {
        var text = PathBox.Text;
        var slash = text.LastIndexOf('\\') + 1;
        var ext = ArchiveTools.DefaultExtension(Format);
        var end = text.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ? text.Length - ext.Length : text.Length;
        PathBox.Select(slash, Math.Max(0, end - slash));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ArchivePath.Length == 0) return;
        DialogResult = true;
    }
}

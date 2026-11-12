using System.Windows;
using ChickenCommander.Core.Tools;

namespace ChickenCommander.Views;

public partial class ChecksumDialog : Window
{
    public ChecksumDialog(int fileCount)
    {
        InitializeComponent();
        MessageText.Text = fileCount == 1 ? "Checksum for 1 file:" : $"Checksums for {fileCount} files:";
        OnePerFileBox.IsEnabled = fileCount > 1;
    }

    public ChecksumKind Kind =>
        Sha512Box.IsChecked == true ? ChecksumKind.Sha512
        : Sha1Box.IsChecked == true ? ChecksumKind.Sha1
        : Md5Box.IsChecked == true ? ChecksumKind.Md5
        : CrcBox.IsChecked == true ? ChecksumKind.Crc32
        : ChecksumKind.Sha256;

    public bool OnePerFile => OnePerFileBox.IsChecked == true;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

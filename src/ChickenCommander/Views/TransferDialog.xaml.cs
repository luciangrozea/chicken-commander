using System.Windows;
using ChickenCommander.Core.Operations;

namespace ChickenCommander.Views;

public partial class TransferDialog : Window
{
    public TransferDialog(TransferKind kind, string description, string initialTarget)
    {
        InitializeComponent();
        Title = kind == TransferKind.Copy ? "Copy" : "Rename / move";
        MessageText.Text = $"{(kind == TransferKind.Copy ? "Copy" : "Move")} {description} to:";
        TargetBox.Text = initialTarget;
        Loaded += (_, _) =>
        {
            TargetBox.Focus();
            TargetBox.SelectAll();
        };
    }

    public string Target => TargetBox.Text;

    public OverwriteMode Mode => (OverwriteMode)OverwriteBox.SelectedIndex;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

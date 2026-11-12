using System.ComponentModel;
using System.Windows;

namespace ChickenCommander.Views;

/// <summary>Non-modal list of running and queued copy/move jobs. Hidden instead of closed, so it can be reused.</summary>
public partial class OperationsWindow : Window
{
    public OperationsWindow() => InitializeComponent();

    /// <summary>Set by the main window when the app is really shutting down.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
}

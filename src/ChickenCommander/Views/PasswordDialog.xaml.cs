using System.Windows;

namespace ChickenCommander.Views;

public partial class PasswordDialog : Window
{
    public PasswordDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        Loaded += (_, _) => Box.Focus();
    }

    public string Password => Box.Password;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

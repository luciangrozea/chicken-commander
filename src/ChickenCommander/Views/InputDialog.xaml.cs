using System.Windows;

namespace ChickenCommander.Views;

public partial class InputDialog : Window
{
    public InputDialog(string title, string message, string initialValue, bool selectNameOnly)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ValueBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            // For renames, pre-select only the name part so typing keeps the extension.
            var dot = initialValue.LastIndexOf('.');
            if (selectNameOnly && dot > 0) ValueBox.Select(0, dot);
            else ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

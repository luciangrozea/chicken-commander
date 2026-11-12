using System.Windows;
using ChickenCommander.ViewModels;

namespace ChickenCommander.Views;

public partial class MultiRenameWindow : Window
{
    public MultiRenameWindow(MultiRenameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ErrorOccurred += msg => MessageBox.Show(this, msg, "Multi-Rename", MessageBoxButton.OK, MessageBoxImage.Warning);
        Loaded += (_, _) =>
        {
            NameMaskBox.Focus();
            NameMaskBox.SelectAll();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

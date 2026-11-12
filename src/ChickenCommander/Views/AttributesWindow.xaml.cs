using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;

namespace ChickenCommander.Views;

public partial class AttributesWindow : Window
{
    private readonly IReadOnlyList<FileEntry> items;
    private readonly (CheckBox Box, FileAttributes Flag)[] flags;

    public AttributesWindow(IReadOnlyList<FileEntry> items)
    {
        InitializeComponent();
        this.items = items;
        flags =
        [
            (ReadOnlyBox, FileAttributes.ReadOnly),
            (ArchiveBox, FileAttributes.Archive),
            (HiddenBox, FileAttributes.Hidden),
            (SystemBox, FileAttributes.System),
        ];

        TargetText.Text = items.Count == 1 ? items[0].Name : $"{items.Count} items";
        foreach (var (box, flag) in flags)
        {
            box.IsChecked = AttributeChanger.Combined(items.Select(i => i.Attributes), flag);
            // Only offer the "mixed" state when it actually exists.
            box.IsThreeState = box.IsChecked is null;
        }

        var first = items[0].Modified;
        DateBox.SelectedDate = first.Date;
        TimeBox.Text = first.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        SubfoldersBox.IsEnabled = items.Any(i => i.IsDirectory);
    }

    public IReadOnlyList<(string Path, string Error)> Errors { get; private set; } = [];

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        DateBox.SelectedDate = now.Date;
        TimeBox.Text = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        FileAttributes set = 0, clear = 0;
        foreach (var (box, flag) in flags)
        {
            if (box.IsChecked == true) set |= flag;
            else if (box.IsChecked == false) clear |= flag;
        }

        DateTime? modified = null;
        if (ChangeDateBox.IsChecked == true)
        {
            if (DateBox.SelectedDate is not { } date ||
                !TimeSpan.TryParseExact(TimeBox.Text.Trim(), [@"hh\:mm\:ss", @"hh\:mm"], CultureInfo.InvariantCulture, out var time))
            {
                MessageBox.Show(this, "Enter a valid date and a time as HH:mm:ss.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            modified = date.Date + time;
        }

        var change = new AttributeChange(set, clear, modified, IncludeSubfolders: SubfoldersBox.IsChecked == true);
        Errors = AttributeChanger.Apply(items.Select(i => i.FullPath), change);
        DialogResult = true;
    }
}

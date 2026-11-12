using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.ViewModels;

namespace ChickenCommander.Views;

public partial class FilePanel : UserControl
{
    public FilePanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    public FilePanelViewModel? ViewModel => DataContext as FilePanelViewModel;

    /// <summary>Raised on Enter/double-click on an item, so the main view model decides how to open it.</summary>
    public event Action<FilePanel>? OpenRequested;

    /// <summary>Raised when a path typed into the path box should be opened.</summary>
    public event Action<FilePanel, string>? PathEntered;

    public bool ListHasFocus => Grid.IsKeyboardFocusWithin;

    public void FocusList() => FocusItem(ViewModel?.CurrentItem);

    public void OpenDriveList()
    {
        DriveBox.IsDropDownOpen = true;
        DriveBox.Focus();
    }

    /// <summary>After picking a drive (or closing the list with Esc) the keyboard goes back to the file list.</summary>
    private void DriveBox_DropDownClosed(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Input);

    public void FocusPathBox()
    {
        PathBox.Focus();
        PathBox.SelectAll();
    }

    /// <summary>Home / End: first or last row (the DataGrid's own Home/End only move between columns).</summary>
    public void MoveCursorToEdge(bool last)
    {
        var vm = ViewModel;
        if (vm is null || vm.Items.Count == 0) return;
        vm.CurrentItem = last ? vm.Items[^1] : vm.Items[0];
        FocusItem(vm.CurrentItem);
    }

    /// <summary>Moves the cursor by <paramref name="delta"/> rows (used after Insert marks an item).</summary>
    public void MoveCursor(int delta)
    {
        var vm = ViewModel;
        if (vm?.CurrentItem is null) return;
        var index = IndexOf(vm.Items, vm.CurrentItem) + delta;
        if (index < 0 || index >= vm.Items.Count) return;
        vm.CurrentItem = vm.Items[index];
        FocusItem(vm.CurrentItem);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FilePanelViewModel oldVm) oldVm.FocusRequested -= OnFocusRequested;
        if (e.NewValue is FilePanelViewModel newVm) newVm.FocusRequested += OnFocusRequested;
    }

    private void OnFocusRequested(FileItemViewModel? item)
    {
        // Only the active panel takes keyboard focus; the other one just updates its listing.
        if (ViewModel is not { IsActive: true }) return;
        Dispatcher.BeginInvoke(() => FocusItem(item), DispatcherPriority.Loaded);
    }

    private void FocusItem(FileItemViewModel? item)
    {
        if (item is null || Grid.Columns.Count == 0)
        {
            Grid.Focus();
            return;
        }
        Grid.SelectedItem = item;
        Grid.ScrollIntoView(item);
        Grid.UpdateLayout();
        Grid.CurrentCell = new DataGridCellInfo(item, Grid.Columns[0]);
        if (Grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row &&
            Grid.Columns[0].GetCellContent(row)?.Parent is DataGridCell cell)
        {
            cell.Focus();
        }
        else
        {
            Grid.Focus();
        }
    }

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // sorting is done by the view model (folders first, natural order)
        var column = e.Column.SortMemberPath switch
        {
            "Ext" => SortColumn.Extension,
            "Size" => SortColumn.Size,
            "Date" => SortColumn.Modified,
            "Attr" => SortColumn.Attributes,
            _ => SortColumn.Name,
        };
        ViewModel?.SortBy(column);
        foreach (var c in Grid.Columns) c.SortDirection = null;
        if (ViewModel is { } vm)
            e.Column.SortDirection = vm.Sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) is not null)
            OpenRequested?.Invoke(this);
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            PathEntered?.Invoke(this, PathBox.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PathBox.Text = ViewModel?.CurrentPath ?? string.Empty;
            FocusList();
            e.Handled = true;
        }
    }

    private void TabStrip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d || FindParent<ListBoxItem>(d) is not { DataContext: PanelTab tab }) return;
        var onCloseButton = IsInsideCloseButton(d);
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && onCloseButton))
        {
            ViewModel?.CloseTab(tab);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            // Do not switch to the tab; its menu opens on button release (TabStrip_PreviewMouseUp).
            e.Handled = true;
        }
        // Clicking a tab also activates this panel.
        Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Input);
    }

    private void TabStrip_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right) return;
        if (e.OriginalSource is not DependencyObject d || FindParent<ListBoxItem>(d) is not { DataContext: PanelTab tab } item) return;
        e.Handled = true;
        ShowTabMenu(tab, item);
    }

    /// <summary>Right-click menu of a tab: close it, close the others, duplicate it.</summary>
    private void ShowTabMenu(PanelTab tab, UIElement target)
    {
        var vm = ViewModel;
        if (vm is null) return;
        var canClose = vm.Tabs.Count > 1;
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };

        var close = new MenuItem { Header = "_Close tab", IsEnabled = canClose };
        close.Click += (_, _) => { vm.CloseTab(tab); FocusList(); };
        var closeOthers = new MenuItem { Header = "Close _other tabs", IsEnabled = canClose };
        closeOthers.Click += (_, _) => { vm.CloseOtherTabs(tab); FocusList(); };
        var duplicate = new MenuItem { Header = "_Duplicate tab" };
        duplicate.Click += (_, _) => { vm.ActiveTab = tab; vm.NewTab(tab.Path); FocusList(); };

        menu.Items.Add(close);
        menu.Items.Add(closeOthers);
        menu.Items.Add(new Separator());
        menu.Items.Add(duplicate);
        menu.IsOpen = true;
    }

    private static bool IsInsideCloseButton(DependencyObject? d)
    {
        for (; d is not null and not ListBoxItem; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: "CloseTab" }) return true;
        return false;
    }

    /// <summary>Screen point just under the path bar, where popup menus (hotlist, history) appear.</summary>
    public UIElement MenuAnchor => PathBox;

    private static int IndexOf(IReadOnlyList<FileItemViewModel> list, FileItemViewModel item)
    {
        for (var i = 0; i < list.Count; i++)
            if (ReferenceEquals(list[i], item)) return i;
        return -1;
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}

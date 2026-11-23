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

    /// <summary>Ctrl+L or a click after the path: the path bar becomes a text box to type a path into.</summary>
    public void FocusPathBox()
    {
        PathBox.Text = ViewModel?.CurrentPath ?? string.Empty;
        Breadcrumb.Visibility = Visibility.Hidden; // keeps its height, so the layout does not jump
        PathBox.Visibility = Visibility.Visible;
        PathBox.Focus();
        PathBox.SelectAll();
    }

    private void ShowBreadcrumb()
    {
        PathBox.Visibility = Visibility.Collapsed;
        Breadcrumb.Visibility = Visibility.Visible;
    }

    private void PathBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ShowBreadcrumb();

    /// <summary>Rebuilds the clickable path: one link per folder, separated by "\" (or "/" on servers).</summary>
    private void BuildBreadcrumb()
    {
        CrumbPanel.Children.Clear();
        var path = ViewModel?.CurrentPath ?? string.Empty;
        var separator = path.Contains("://", StringComparison.Ordinal) ? "/" : "\\";
        var segments = PathHelper.Segments(path);
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0) CrumbPanel.Children.Add(new TextBlock { Text = separator, Opacity = 0.6, Margin = new Thickness(1, 0, 1, 0) });
            var segment = segments[i];
            var isLast = i == segments.Count - 1;
            var link = new TextBlock { Text = segment.Label, Tag = segment, Cursor = isLast ? Cursors.IBeam : Cursors.Hand };
            if (!isLast)
            {
                link.ToolTip = segment.Path;
                link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
                link.MouseLeave += (_, _) => link.TextDecorations = null;
            }
            CrumbPanel.Children.Add(link);
        }
        // Long paths: keep the end (the current folder) in view.
        Dispatcher.BeginInvoke(CrumbScroller.ScrollToRightEnd, DispatcherPriority.Loaded);
    }

    private void Breadcrumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var vm = ViewModel;
        if (vm is null) return;
        var segments = PathHelper.Segments(vm.CurrentPath);
        // A folder before the current one: go there, with the cursor on the folder we came from.
        if (e.OriginalSource is TextBlock { Tag: PathSegment segment } && segments.Count > 0 && segment != segments[^1])
        {
            var index = segments.ToList().IndexOf(segment);
            var cameFrom = index >= 0 && index + 1 < segments.Count ? segments[index + 1].Label : null;
            if (vm.Navigate(segment.Path, cameFrom)) FocusList();
            return;
        }
        // The current folder or the empty space after the path: type a path.
        FocusPathBox();
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
        if (e.OldValue is FilePanelViewModel oldVm)
        {
            oldVm.FocusRequested -= OnFocusRequested;
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        if (e.NewValue is FilePanelViewModel newVm)
        {
            newVm.FocusRequested += OnFocusRequested;
            newVm.PropertyChanged += OnViewModelPropertyChanged;
        }
        BuildBreadcrumb();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilePanelViewModel.CurrentPath)) BuildBreadcrumb();
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

    // ---- Explorer-style selection: Ctrl+click, Shift+click, Shift+arrows ---------------------

    /// <summary>Where a Shift range starts: the item last clicked or reached without Shift.</summary>
    private FileItemViewModel? anchor;

    /// <summary>
    /// Raised on right-click (or the menu key) with a point relative to this panel and the item under it
    /// (null on empty space): the main window shows the Explorer menu for the selection.
    /// </summary>
    public event Action<FilePanel, Point, FileItemViewModel?>? ContextMenuRequested;

    private FileItemViewModel? ItemAt(object originalSource) =>
        originalSource is DependencyObject d && FindParent<DataGridRow>(d) is { Item: FileItemViewModel item } ? item : null;

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || ItemAt(e.OriginalSource) is not { } item) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift))
        {
            vm.MarkRange(anchor ?? vm.CurrentItem ?? item, item, keepOthers: mods.HasFlag(ModifierKeys.Control));
            MoveCursorTo(item, keepAnchor: true);
            e.Handled = true;
        }
        else if (mods.HasFlag(ModifierKeys.Control))
        {
            vm.ToggleMark(item);
            MoveCursorTo(item, keepAnchor: false);
            e.Handled = true;
        }
        else if (e.ClickCount == 1 && vm.MarkedItems.Any())
        {
            // A plain click selects only the clicked item, as in Explorer.
            vm.MarkAll(false);
        }
    }

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null) return;
        e.Handled = true; // the DataGrid would otherwise move its own selection
        if (ItemAt(e.OriginalSource) is not { } item) return;
        // Right-click on a marked item keeps the marks (the menu acts on all of them);
        // on any other item it selects only that one, as in Explorer.
        if (!item.IsMarked && vm.MarkedItems.Any()) vm.MarkAll(false);
        MoveCursorTo(item, keepAnchor: false);
    }

    private void Grid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var item = ItemAt(e.OriginalSource);
        ContextMenuRequested?.Invoke(this, e.GetPosition(this), item);
    }

    /// <summary>Menu key / Shift+F10: the menu opens next to the cursor row.</summary>
    public void RequestContextMenuAtCursor()
    {
        var vm = ViewModel;
        var item = vm?.CurrentItem;
        var point = new Point(40, 40);
        if (item is not null && Grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
            point = row.TranslatePoint(new Point(40, row.ActualHeight), this);
        ContextMenuRequested?.Invoke(this, point, item);
    }

    private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        if (vm.CurrentItem is not { } current || vm.Items.Count == 0) return;
        var index = vm.IndexOfItem(current);
        var page = Math.Max(1, (int)(Grid.ActualHeight / Math.Max(16, Grid.RowHeight is > 0 and var h ? h : 20)) - 2);
        int target = e.Key switch
        {
            Key.Up => index - 1,
            Key.Down => index + 1,
            Key.PageUp => index - page,
            Key.PageDown => index + page,
            Key.Home => 0,
            Key.End => vm.Items.Count - 1,
            _ => int.MinValue,
        };
        if (target == int.MinValue) return;
        e.Handled = true;
        target = Math.Clamp(target, 0, vm.Items.Count - 1);
        var item = vm.Items[target];
        vm.MarkRange(anchor ?? current, item, keepOthers: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        MoveCursorTo(item, keepAnchor: true);
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Moving without Shift (arrows, clicks, navigation) sets where the next Shift range starts.
        if (!keepAnchorOnce && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) anchor = ViewModel?.CurrentItem;
        keepAnchorOnce = false;
    }

    private bool keepAnchorOnce;

    private void MoveCursorTo(FileItemViewModel item, bool keepAnchor)
    {
        if (ViewModel is not { } vm) return;
        keepAnchorOnce = keepAnchor && !ReferenceEquals(vm.CurrentItem, item);
        if (!keepAnchor) anchor = item;
        vm.CurrentItem = item;
        FocusItem(item);
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
        else if (e.ChangedButton == MouseButton.Left && ViewModel is { } vm)
        {
            // The tab items are not focusable (the keyboard stays in the file list), and a ListBoxItem
            // only selects itself when it can take focus, so the click switches tabs here.
            vm.ActiveTab = tab;
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
    public UIElement MenuAnchor => Breadcrumb;

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

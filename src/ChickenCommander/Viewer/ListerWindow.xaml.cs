using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ChickenCommander.Viewer;

/// <summary>
/// The F3 viewer. Keys: 1/2/3 text/hex/image, W wrap, Ctrl+F find, F3 find next, N/P next/previous file,
/// Ctrl+C copy, Esc close.
/// </summary>
public partial class ListerWindow : Window
{
    private readonly IReadOnlyList<string> files;
    private int index;
    private bool switchingMode;

    public ListerWindow(IReadOnlyList<string> files, int startIndex)
    {
        InitializeComponent();
        this.files = files;
        index = startIndex;
        Viewer.MaxImageSide = 8192; // the full viewer shows more detail than the Quick View panel
        Viewer.InfoChanged += () => InfoText.Text = Viewer.Info;
        Loaded += async (_, _) => await ShowFileAsync(ViewerMode.Auto);
    }

    private async Task ShowFileAsync(ViewerMode mode)
    {
        var path = files[index];
        Title = $"Lister – {path}";
        PositionText.Text = files.Count > 1 ? $"{index + 1} / {files.Count}  (N / P)" : string.Empty;
        await Viewer.LoadAsync(path, mode);

        switchingMode = true;
        TextMode.IsChecked = Viewer.ActualMode == ViewerMode.Text;
        HexMode.IsChecked = Viewer.ActualMode == ViewerMode.Hex;
        ImageMode.IsChecked = Viewer.ActualMode == ViewerMode.Image;
        switchingMode = false;
        Viewer.FocusContent();
    }

    private async void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (switchingMode) return;
        var mode = sender == HexMode ? ViewerMode.Hex : sender == ImageMode ? ViewerMode.Image : ViewerMode.Text;
        if (mode != Viewer.ActualMode) await ShowFileAsync(mode);
    }

    private void Wrap_Changed(object sender, RoutedEventArgs e) => Viewer.WordWrap = WrapBox.IsChecked == true;

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();

    private async void FindNext()
    {
        if (FindBox.Text.Length == 0)
        {
            FindBox.Focus();
            return;
        }
        if (Viewer.IsSearching) return; // one search at a time; Esc stops it
        var text = FindBox.Text;
        var info = InfoText.Text;
        InfoText.Text = $"Searching for \"{text}\"…  (Esc to stop)";
        bool found;
        try
        {
            found = await Viewer.FindNextAsync(text, MatchCaseBox.IsChecked == true);
        }
        finally
        {
            if (IsLoaded) InfoText.Text = info;
        }
        if (!found && !stoppedSearch && IsLoaded)
            MessageBox.Show(this, $"\"{text}\" was not found.", "Find", MessageBoxButton.OK, MessageBoxImage.Information);
        stoppedSearch = false;
    }

    private bool stoppedSearch;

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext();
            e.Handled = true;
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var inFindBox = Keyboard.FocusedElement == FindBox;
        var mods = Keyboard.Modifiers;

        switch (e.Key)
        {
            case Key.Escape when Viewer.IsSearching:
                stoppedSearch = true;
                Viewer.CancelSearch();
                e.Handled = true;
                return;
            case Key.Escape when inFindBox:
                Viewer.FocusContent();
                e.Handled = true;
                return;
            case Key.Escape:
                Close();
                e.Handled = true;
                return;
            case Key.F3 when mods == ModifierKeys.None:
            case Key.F7 when mods == ModifierKeys.None:
                if (e.Key == Key.F7 || FindBox.Text.Length == 0) FocusFind();
                else FindNext();
                e.Handled = true;
                return;
            case Key.F when mods == ModifierKeys.Control:
                FocusFind();
                e.Handled = true;
                return;
        }

        if (inFindBox || mods != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1: TextMode.IsChecked = true; e.Handled = true; break;
            case Key.D2 or Key.NumPad2: HexMode.IsChecked = true; e.Handled = true; break;
            case Key.D3 or Key.NumPad3: ImageMode.IsChecked = true; e.Handled = true; break;
            case Key.W: WrapBox.IsChecked = WrapBox.IsChecked != true; e.Handled = true; break;
            case Key.N when index < files.Count - 1:
                index++;
                e.Handled = true;
                await ShowFileAsync(ViewerMode.Auto);
                break;
            case Key.P when index > 0:
                index--;
                e.Handled = true;
                await ShowFileAsync(ViewerMode.Auto);
                break;
        }
    }

    private void FocusFind()
    {
        FindBox.Focus();
        FindBox.SelectAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        Viewer.Close();
        base.OnClosed(e);
    }
}

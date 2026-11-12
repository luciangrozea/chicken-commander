using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ChickenCommander.Commands;
using ChickenCommander.Core.Settings;
using Microsoft.Win32;

namespace ChickenCommander.Views;

/// <summary>Appearance, operations, keyboard shortcuts and the button bar. Changes are applied to the settings on OK only.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings settings;
    private readonly List<ShortcutRow> rows;
    private readonly ObservableCollection<ButtonRow> buttons;
    private string? captured;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;

        // Appearance & operations
        ThemeBox.SelectedItem = ThemeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == settings.Theme) ?? ThemeBox.Items[0];
        FontSizeSlider.Value = settings.ListFontSize;
        ButtonBarBox.IsChecked = settings.ShowButtonBar;
        RecycleBinBox.IsChecked = settings.DeleteToRecycleBin;
        ConfirmDeleteBox.IsChecked = settings.ConfirmDelete;

        // Keyboard: a working copy of every command's gestures.
        var map = new ShortcutMap(CommandRegistry.DefaultGestures, settings.Shortcuts);
        rows = CommandRegistry.All.Select(c => new ShortcutRow(c, map.GesturesFor(c.Id).ToList())).ToList();
        CommandsGrid.ItemsSource = rows;
        CommandsGrid.SelectedIndex = 0;
        UpdateConflicts();

        // Button bar
        buttons = new ObservableCollection<ButtonRow>((settings.ButtonBar ?? CommandRegistry.DefaultButtonBar()).Select(b => new ButtonRow(b)));
        ButtonsList.ItemsSource = buttons;
        CommandPicker.ItemsSource = CommandRegistry.All;
        CommandPicker.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        settings.Theme = (string)((ComboBoxItem)ThemeBox.SelectedItem).Tag;
        settings.ListFontSize = FontSizeSlider.Value;
        settings.ShowButtonBar = ButtonBarBox.IsChecked == true;
        settings.DeleteToRecycleBin = RecycleBinBox.IsChecked == true;
        settings.ConfirmDelete = ConfirmDeleteBox.IsChecked == true;
        settings.Shortcuts = ShortcutMap.Diff(CommandRegistry.DefaultGestures, rows.ToDictionary(r => r.Command.Id, r => r.Gestures));

        var bar = buttons.Select(b => b.Item).ToList();
        var isDefault = bar.SequenceEqual(CommandRegistry.DefaultButtonBar());
        settings.ButtonBar = isDefault ? null : bar;
        DialogResult = true;
    }

    // ---- Keyboard --------------------------------------------------------------------------

    private ShortcutRow? Selected => CommandsGrid.SelectedItem as ShortcutRow;

    private void CommandsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedCommandText.Text = Selected is { } r ? $"{r.Category} › {r.Title}" : string.Empty;
        captured = null;
        CaptureBox.Text = string.Empty;
    }

    private void CaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Every key is captured here (including Tab and Alt combinations) instead of acting on the dialog.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (KeyNames.From(key, Keyboard.Modifiers) is not { } gesture) return;
        if (key is Key.Enter or Key.Back or Key.Space or Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            ConflictText.Text = "Enter, Backspace, Space and Esc on their own are reserved.";
            return;
        }
        captured = gesture;
        CaptureBox.Text = KeyNames.Display(gesture);
        var owner = rows.FirstOrDefault(r => r != Selected && r.Gestures.Contains(gesture, StringComparer.OrdinalIgnoreCase));
        ConflictText.Text = owner is null ? string.Empty : $"{KeyNames.Display(gesture)} is used by “{owner.Title}”. Assigning it here removes it there.";
    }

    private void Assign_Click(object sender, RoutedEventArgs e) => SetGesture(replace: true);

    private void AddGesture_Click(object sender, RoutedEventArgs e) => SetGesture(replace: false);

    private void SetGesture(bool replace)
    {
        if (Selected is not { } row || captured is null) return;
        foreach (var other in rows.Where(r => r != row)) other.Remove(captured); // one gesture, one command
        row.Set(replace ? [captured] : [.. row.Gestures.Where(g => !g.Equals(captured, StringComparison.OrdinalIgnoreCase)), captured]);
        captured = null;
        CaptureBox.Text = string.Empty;
        UpdateConflicts();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Selected?.Set([]);
        UpdateConflicts();
    }

    private void ResetOne_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        row.Set([.. row.Command.DefaultGestures.Select(Gesture.Normalize).OfType<string>()]);
        UpdateConflicts();
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows) row.Set([.. row.Command.DefaultGestures.Select(Gesture.Normalize).OfType<string>()]);
        UpdateConflicts();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var f = FilterBox.Text.Trim();
        CommandsGrid.ItemsSource = f.Length == 0 ? rows : rows.Where(r =>
            r.Title.Contains(f, StringComparison.CurrentCultureIgnoreCase) || r.GestureText.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Category.Contains(f, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void UpdateConflicts()
    {
        var map = new ShortcutMap(rows.ToDictionary(r => r.Command.Id, r => r.Gestures.ToArray()));
        var conflicts = map.Conflicts();
        ConflictText.Text = conflicts.Count == 0 ? string.Empty
            : "Conflicts: " + string.Join("; ", conflicts.Select(c => $"{KeyNames.Display(c.Gesture)} → {string.Join(", ", c.Commands)}"));
    }

    // ---- Button bar ------------------------------------------------------------------------

    private void AddCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (CommandPicker.SelectedItem is CommandDefinition c) Insert(ButtonBarItem.ForCommand(c.Id));
    }

    private void AddProgramButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProgramBox.Text)) return;
        Insert(new ButtonBarItem
        {
            Kind = ButtonKind.Program,
            Program = ProgramBox.Text.Trim(),
            Arguments = ArgumentsBox.Text,
            Label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim(),
        });
        ProgramBox.Clear();
        LabelBox.Clear();
    }

    private void AddSeparator_Click(object sender, RoutedEventArgs e) => Insert(ButtonBarItem.Separator);

    private void Insert(ButtonBarItem item)
    {
        var index = ButtonsList.SelectedIndex >= 0 ? ButtonsList.SelectedIndex + 1 : buttons.Count;
        buttons.Insert(index, new ButtonRow(item));
        ButtonsList.SelectedIndex = index;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var i = ButtonsList.SelectedIndex;
        if (i < 0) return;
        buttons.RemoveAt(i);
        ButtonsList.SelectedIndex = Math.Min(i, buttons.Count - 1);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        var i = ButtonsList.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= buttons.Count) return;
        buttons.Move(i, j);
        ButtonsList.SelectedIndex = j;
    }

    private void ResetBar_Click(object sender, RoutedEventArgs e)
    {
        buttons.Clear();
        foreach (var b in CommandRegistry.DefaultButtonBar()) buttons.Add(new ButtonRow(b));
    }

    private void BrowseProgram_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Program", Filter = "Programs|*.exe;*.cmd;*.bat;*.ps1|All files|*.*" };
        if (dialog.ShowDialog(this) == true) ProgramBox.Text = dialog.FileName;
    }

    /// <summary>A command with its editable list of gestures.</summary>
    private sealed class ShortcutRow(CommandDefinition command, List<string> gestures) : INotifyPropertyChanged
    {
        public CommandDefinition Command { get; } = command;
        public List<string> Gestures { get; private set; } = gestures;
        public string Category => Command.Category;
        public string Title => Command.Title;
        public string GestureText => string.Join(" / ", Gestures.Select(KeyNames.Display));

        public void Set(List<string> gestures)
        {
            Gestures = gestures;
            Changed(nameof(GestureText));
        }

        public void Remove(string gesture)
        {
            if (Gestures.RemoveAll(g => g.Equals(gesture, StringComparison.OrdinalIgnoreCase)) > 0) Changed(nameof(GestureText));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Display wrapper for a button bar item.</summary>
    private sealed record ButtonRow(ButtonBarItem Item)
    {
        public string Glyph => Item.Kind switch
        {
            ButtonKind.Separator => string.Empty,
            ButtonKind.Command => Item.Glyph ?? CommandRegistry.Find(Item.CommandId)?.Glyph ?? string.Empty,
            _ => Item.Glyph ?? CommandRegistry.ProgramGlyph,
        };

        public string Text => Item.Kind switch
        {
            ButtonKind.Separator => "──────── separator ────────",
            ButtonKind.Command => CommandRegistry.Find(Item.CommandId)?.Title ?? $"(unknown: {Item.CommandId})",
            _ => $"{Item.Label ?? Path.GetFileName(Item.Program)}   —   {Item.Program} {Item.Arguments}",
        };
    }
}

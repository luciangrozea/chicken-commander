using ChickenCommander.Core.FileSystem;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChickenCommander.ViewModels;

/// <summary>A folder tab: its own path, cursor position and back/forward history.</summary>
public partial class PanelTab(string path) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string path = path;

    public NavigationHistory History { get; } = new();

    /// <summary>Name under the cursor when the tab was left, restored when it comes back.</summary>
    public string? CursorName { get; set; }

    public string Title
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }
}

using System.Windows.Media;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChickenCommander.ViewModels;

public partial class FileItemViewModel(FileEntry entry) : ObservableObject
{
    private ImageSource? icon;
    private bool iconLoaded;

    [ObservableProperty]
    private bool isMarked;

    public FileEntry Entry { get; } = entry;

    public string Name => Entry.BaseName;
    public string Ext => Entry.Extension;
    public bool IsHidden => Entry.IsHidden;
    public bool IsParentLink => Entry.IsParentLink;

    public string SizeText =>
        Entry.IsParentLink ? "<UP>" : Entry.IsDirectory ? "<DIR>" : SizeFormatter.Exact(Entry.Size);

    public string DateText => Entry.IsParentLink ? string.Empty : Entry.Modified.ToString("dd.MM.yyyy HH:mm");

    public string Attr => Entry.IsParentLink ? string.Empty : Entry.AttributeString;

    /// <summary>Loaded on first access so only visible (virtualized) rows hit the shell.</summary>
    public ImageSource? Icon
    {
        get
        {
            if (!iconLoaded)
            {
                icon = ShellIcons.For(Entry);
                iconLoaded = true;
            }
            return icon;
        }
    }
}

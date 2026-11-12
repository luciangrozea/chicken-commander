using ChickenCommander.Core.FileSystem;

namespace ChickenCommander.ViewModels;

public sealed record DriveItem(string Root, string Label, DriveType Type)
{
    public string Letter => Root.TrimEnd('\\').ToLowerInvariant();

    public string Description => string.IsNullOrEmpty(Label) ? $"{Letter} [{Type}]" : $"{Letter} [{Label}]";

    public static IReadOnlyList<DriveItem> Enumerate() =>
        DriveInfo.GetDrives().Select(d =>
        {
            string label;
            try { label = d.IsReady ? d.VolumeLabel : string.Empty; }
            catch (IOException) { label = string.Empty; }
            catch (UnauthorizedAccessException) { label = string.Empty; }
            return new DriveItem(d.Name, label, d.DriveType);
        }).ToList();

    public string? FreeSpaceText()
    {
        try
        {
            var d = new DriveInfo(Root);
            if (!d.IsReady) return "not ready";
            return $"[{Label}]  {SizeFormatter.Human(d.AvailableFreeSpace)} free of {SizeFormatter.Human(d.TotalSize)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

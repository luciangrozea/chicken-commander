using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChickenCommander.Core.Settings;

public sealed record HotlistEntry(string Name, string Path);

public sealed class PanelSettings
{
    public List<string> Tabs { get; set; } = [];
    public int ActiveTab { get; set; }
}

public sealed class WindowSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; }
}

/// <summary>Everything that survives a restart. Stored as JSON in %AppData%\ChickenCommander\settings.json.</summary>
public sealed class AppSettings
{
    public PanelSettings Left { get; set; } = new();
    public PanelSettings Right { get; set; } = new();
    public bool LeftIsActive { get; set; } = true;
    public bool ShowHidden { get; set; }
    public List<HotlistEntry> Hotlist { get; set; } = [];
    public WindowSettings Window { get; set; } = new();
}

public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, // NaN window position = "not set"
    };

    public static SettingsStore Default => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChickenCommander", "settings.json"));

    public string FilePath { get; } = filePath;

    /// <summary>Loads settings; a missing or corrupt file gives defaults instead of an error.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes atomically (temp file + replace) so a crash never leaves a half-written file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}

using ChickenCommander.Core.Settings;

namespace ChickenCommander.Core.Tests;

public class GestureTests
{
    [Theory]
    [InlineData("shift+ctrl+f5", "Ctrl+Shift+F5")]
    [InlineData(" Alt + left ", "Alt+Left")]
    [InlineData("control+q", "Ctrl+Q")]
    [InlineData("F3", "F3")]
    [InlineData("Ctrl++", "Ctrl++")]
    [InlineData("Ctrl+Shift", null)]
    [InlineData("", null)]
    [InlineData("A+B", null)]
    public void Normalizes(string input, string? expected) => Assert.Equal(expected, Gesture.Normalize(input));
}

public class ShortcutMapTests
{
    private static readonly Dictionary<string, string[]> Defaults = new()
    {
        ["Copy"] = ["F5"],
        ["Search"] = ["Alt+F7"],
        ["Refresh"] = ["Ctrl+R"],
    };

    [Fact]
    public void Defaults_and_overrides()
    {
        var map = new ShortcutMap(Defaults, new Dictionary<string, List<string>> { ["Search"] = ["ctrl+f"], ["Refresh"] = [] });
        Assert.Equal("Copy", map.CommandFor("f5"));
        Assert.Equal("Search", map.CommandFor("Ctrl+F"));
        Assert.Null(map.CommandFor("Alt+F7"));
        Assert.Null(map.CommandFor("Ctrl+R"));
        Assert.Empty(map.GesturesFor("Refresh"));
        Assert.Empty(map.Conflicts());
    }

    [Fact]
    public void Reports_conflicts_and_first_definition_wins()
    {
        var map = new ShortcutMap(Defaults, new Dictionary<string, List<string>> { ["Refresh"] = ["F5"] });
        Assert.Equal("Copy", map.CommandFor("F5"));
        var conflict = Assert.Single(map.Conflicts());
        Assert.Equal("F5", conflict.Gesture);
        Assert.Equal(["Copy", "Refresh"], conflict.Commands);
    }

    [Fact]
    public void Diff_keeps_only_changes()
    {
        var current = new Dictionary<string, List<string>> { ["Copy"] = ["F5"], ["Search"] = ["Ctrl+F"], ["Refresh"] = ["ctrl+r"] };
        var diff = ShortcutMap.Diff(Defaults, current);
        Assert.Equal(["Search"], diff.Keys);
        Assert.Equal(["Ctrl+F"], diff["Search"]);
    }
}

public class ButtonArgumentsTests
{
    [Fact]
    public void Expands_placeholders_and_quotes_paths_with_spaces()
    {
        var result = ButtonArguments.Expand("/c %F %N %S -> %T 100%%", @"C:\My Files", "a b.txt", [@"C:\x\1.txt", @"C:\y y\2.txt"], @"D:\out");
        Assert.Equal(@"/c ""C:\My Files\a b.txt"" ""a b.txt"" C:\x\1.txt ""C:\y y\2.txt"" -> D:\out 100%", result);
    }

    [Fact]
    public void Does_not_double_quote_inside_quotes()
    {
        Assert.Equal(@"""C:\My Files""", ButtonArguments.Expand("\"%P\"", @"C:\My Files", null, [], ""));
        Assert.Equal("%X stays", ButtonArguments.Expand("%X stays", "", null, [], ""));
        // Drive root inside quotes: the trailing backslash must not escape the closing quote.
        Assert.Equal(@"""D:\\""", ButtonArguments.Expand("\"%P\"", @"D:\", null, [], ""));
        Assert.Equal(@"D:\", ButtonArguments.Expand("%P", @"D:\", null, [], ""));
    }
}

public class AppSettingsV1Tests : TempFolderTest
{
    [Fact]
    public void New_settings_round_trip()
    {
        var store = new SettingsStore(P("s.json"));
        var s = new AppSettings { Theme = "Dark", ListFontSize = 15, ConfirmDelete = false };
        s.Shortcuts["Search"] = ["Ctrl+F"];
        s.ButtonBar = [ButtonBarItem.ForCommand("Refresh"), ButtonBarItem.Separator, new ButtonBarItem { Kind = ButtonKind.Program, Program = "notepad.exe", Arguments = "%F" }];
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal(15, loaded.ListFontSize);
        Assert.False(loaded.ConfirmDelete);
        Assert.Equal(["Ctrl+F"], loaded.Shortcuts["Search"]);
        Assert.Equal(ButtonKind.Program, loaded.ButtonBar![2].Kind);
        Assert.Equal("%F", loaded.ButtonBar[2].Arguments);
    }
}

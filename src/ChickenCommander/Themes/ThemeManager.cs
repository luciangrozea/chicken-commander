using System.Windows;
using Microsoft.Win32;

namespace ChickenCommander.Themes;

/// <summary>
/// Switches between the light and dark colour dictionaries at runtime, and sets WPF's built-in
/// Fluent theme mode so standard controls (buttons, text boxes, menus, scroll bars) follow too.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static readonly string[] Choices = ["System", "Light", "Dark"];

    /// <summary>Applies "System", "Light" or "Dark". "System" follows the Windows app theme.</summary>
    public static void Apply(string? choice)
    {
        var dark = choice switch
        {
            "Dark" => true,
            "Light" => false,
            _ => WindowsUsesDarkTheme(),
        };

        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;
        var uri = new Uri(dark ? "/Themes/Dark.xaml" : "/Themes/Light.xaml", UriKind.Relative);
        var current = dictionaries.FirstOrDefault(d => d.Source?.OriginalString.StartsWith("/Themes/", StringComparison.Ordinal) == true);
        if (current?.Source != uri)
        {
            if (current is not null) dictionaries.Remove(current);
            dictionaries.Insert(0, new ResourceDictionary { Source = uri });
        }

        // Always set: App.xaml starts with the light colours, but the Fluent control theme still has to be switched on.
#pragma warning disable WPF0001 // ThemeMode is marked experimental; it is the supported way to get Fluent light/dark controls.
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001
        IsDark = dark;
    }

    public static bool IsDark { get; private set; }

    private static bool WindowsUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

using System.Windows.Input;
using ChickenCommander.Core.Settings;

namespace ChickenCommander.Commands;

/// <summary>Turns WPF key events into the gesture strings used by <see cref="ShortcutMap"/> ("Ctrl+Shift+F5").</summary>
public static class KeyNames
{
    /// <summary>Gesture text for a key press, or null for a bare modifier key.</summary>
    public static string? From(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None) return null;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(Name(key));
        return Gesture.Normalize(string.Join('+', parts));
    }

    /// <summary>
    /// Stable, readable key names. Some WPF keys share a value (PageDown = Next, Enter = Return), so
    /// <c>Key.ToString()</c> is not reliable; '+' is avoided because it separates gesture parts.
    /// </summary>
    public static string Name(Key key) => key switch
    {
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Enter => "Enter",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.Add => "NumPlus",
        Key.Subtract => "NumMinus",
        Key.Multiply => "NumMultiply",
        Key.Divide => "NumDivide",
        Key.Decimal => "NumDecimal",
        Key.Oem5 => "Backslash",
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        Key.OemMinus => "Minus",
        Key.OemPlus => "Equals",
        Key.CapsLock => "CapsLock",
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + ((int)key - (int)Key.NumPad0),
        _ => key.ToString(),
    };

    /// <summary>Friendlier text for menus and the shortcut list ("Num +" instead of "NumPlus").</summary>
    public static string Display(string gesture) => gesture
        .Replace("NumPlus", "Num +").Replace("NumMinus", "Num -").Replace("NumMultiply", "Num *")
        .Replace("Backslash", "\\").Replace("PageUp", "PgUp").Replace("PageDown", "PgDn").Replace("Comma", ",");

    /// <summary>
    /// While a text box has focus, only gestures that cannot be typed text are treated as commands:
    /// anything with Ctrl or Alt, and function keys / Tab.
    /// </summary>
    public static bool AllowedInTextBox(Key key, ModifierKeys modifiers) =>
        modifiers.HasFlag(ModifierKeys.Control) || modifiers.HasFlag(ModifierKeys.Alt)
        || key is >= Key.F1 and <= Key.F24 || key == Key.Tab;
}

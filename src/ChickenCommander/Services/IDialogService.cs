namespace ChickenCommander.Services;

public interface IDialogService
{
    /// <summary>Window handle used as owner for native shell dialogs.</summary>
    IntPtr OwnerHandle { get; }

    /// <summary>Asks for a line of text; returns null when cancelled.</summary>
    string? Prompt(string title, string message, string initialValue, bool selectNameOnly = false);

    bool Confirm(string title, string message);

    void ShowError(string title, string message);

    void ShowAbout();
}

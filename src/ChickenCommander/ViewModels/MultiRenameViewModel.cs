using System.ComponentModel;
using ChickenCommander.Core.Operations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChickenCommander.ViewModels;

public partial class MultiRenameViewModel : ObservableObject
{
    private IReadOnlyList<RenameSource> sources;
    private IReadOnlyList<(string NewPath, string OldPath)>? lastBatch;

    [ObservableProperty] private string nameMask = "[N]";
    [ObservableProperty] private string extensionMask = "[E]";
    [ObservableProperty] private string search = string.Empty;
    [ObservableProperty] private string replace = string.Empty;
    [ObservableProperty] private bool useRegex;
    [ObservableProperty] private bool matchCase;
    [ObservableProperty] private CaseMode caseMode = CaseMode.Unchanged;
    [ObservableProperty] private int counterStart = 1;
    [ObservableProperty] private int counterStep = 1;
    [ObservableProperty] private int counterDigits = 1;
    [ObservableProperty] private IReadOnlyList<RenamePreview> previews = [];
    [ObservableProperty] private string summary = string.Empty;

    public MultiRenameViewModel(IReadOnlyList<RenameSource> sources)
    {
        this.sources = sources;
        UpdatePreview();
    }

    public IReadOnlyList<CaseMode> CaseModes { get; } = Enum.GetValues<CaseMode>();

    public bool AnyRenamed { get; private set; }

    /// <summary>Raised to show an error message (the view decides how).</summary>
    public event Action<string>? ErrorOccurred;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Previews) or nameof(Summary))) UpdatePreview();
    }

    /// <summary>Inserts a placeholder into the name mask (from the helper buttons).</summary>
    [RelayCommand]
    private void InsertPlaceholder(string placeholder) => NameMask += placeholder;

    [RelayCommand]
    private void Reset()
    {
        NameMask = "[N]";
        ExtensionMask = "[E]";
        Search = Replace = string.Empty;
        UseRegex = MatchCase = false;
        CaseMode = CaseMode.Unchanged;
        CounterStart = CounterStep = CounterDigits = 1;
    }

    [RelayCommand]
    private void Start()
    {
        if (Previews.Any(p => p.Error is not null))
        {
            ErrorOccurred?.Invoke("Some names have errors (shown in red). Fix them before renaming.");
            return;
        }
        try
        {
            lastBatch = MultiRename.Execute(Previews);
            AnyRenamed |= lastBatch.Count > 0;
            // The renamed files are the new sources, so the user can keep refining.
            var map = lastBatch.ToDictionary(b => b.OldPath, b => b.NewPath, StringComparer.OrdinalIgnoreCase);
            sources = sources.Select(s => map.TryGetValue(s.FullPath, out var n) ? s with { FullPath = n } : s).ToList();
            Reset();
            UpdatePreview();
            Summary = $"Renamed {lastBatch.Count} item(s). Undo is available.";
            UndoCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (lastBatch is null) return;
        try
        {
            MultiRename.Undo(lastBatch);
            var map = lastBatch.ToDictionary(b => b.NewPath, b => b.OldPath, StringComparer.OrdinalIgnoreCase);
            sources = sources.Select(s => map.TryGetValue(s.FullPath, out var o) ? s with { FullPath = o } : s).ToList();
            lastBatch = null;
            UpdatePreview();
            Summary = "Undo done.";
            UndoCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
    }

    private bool CanUndo() => lastBatch is { Count: > 0 };

    private void UpdatePreview()
    {
        var options = new RenameOptions
        {
            NameMask = NameMask,
            ExtensionMask = ExtensionMask,
            Search = Search,
            Replace = Replace,
            UseRegex = UseRegex,
            MatchCase = MatchCase,
            Case = CaseMode,
            CounterStart = CounterStart,
            CounterStep = CounterStep,
            CounterDigits = Math.Clamp(CounterDigits, 1, 10),
        };
        Previews = MultiRename.Preview(options, sources);
        var errors = Previews.Count(p => p.Error is not null);
        var changes = Previews.Count(p => p.Changes);
        Summary = errors > 0 ? $"{errors} error(s)" : $"{changes} of {Previews.Count} item(s) will be renamed";
    }
}

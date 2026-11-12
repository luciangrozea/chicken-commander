using System.Diagnostics;
using System.Windows;
using ChickenCommander.Core.FileSystem;
using ChickenCommander.Core.Operations;
using ChickenCommander.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChickenCommander.ViewModels;

public enum OperationState { Queued, Running, Paused, Done, Cancelled, Failed }

/// <summary>One copy/move job in the operation queue. Runs on a background thread; the UI polls its progress.</summary>
public partial class TransferOperationViewModel : ObservableObject, ITransferCallbacks
{
    private readonly IDialogService dialogs;
    private readonly PauseController pause = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Stopwatch clock = new();
    private volatile TransferProgress? latest;

    [ObservableProperty] private OperationState state = OperationState.Queued;
    [ObservableProperty] private string currentFile = string.Empty;
    [ObservableProperty] private double filePercent;
    [ObservableProperty] private double totalPercent;
    [ObservableProperty] private string detailText = "Waiting…";

    public TransferOperationViewModel(TransferRequest request, IDialogService dialogs)
    {
        Request = request;
        this.dialogs = dialogs;
        var what = request.Sources.Count == 1 ? Path.GetFileName(request.Sources[0].TrimEnd('\\')) : $"{request.Sources.Count} items";
        Title = $"{(request.Kind == TransferKind.Copy ? "Copy" : "Move")} {what} → {request.Target}";
    }

    public TransferRequest Request { get; }
    public string Title { get; }
    public TransferResult? Result { get; private set; }

    public bool IsFinished => State is OperationState.Done or OperationState.Cancelled or OperationState.Failed;

    public async Task RunAsync()
    {
        State = OperationState.Running;
        clock.Start();
        try
        {
            Result = await Task.Run(() => FileTransfer.RunAsync(Request, this, pause, cts.Token));
            State = Result.Cancelled ? OperationState.Cancelled : OperationState.Done;
            RefreshProgress();
            DetailText = Result.Cancelled
                ? "Cancelled"
                : $"Done: {Result.FilesDone} file(s)" + (Result.Skipped > 0 ? $", {Result.Skipped} skipped" : "") + (Result.Errors > 0 ? $", {Result.Errors} error(s)" : "");
        }
        catch (OperationCanceledException)
        {
            State = OperationState.Cancelled;
            DetailText = "Cancelled";
        }
        catch (Exception ex)
        {
            State = OperationState.Failed;
            DetailText = ex.Message;
        }
        finally
        {
            clock.Stop();
            OnPropertyChanged(nameof(IsFinished));
        }
    }

    /// <summary>Called by the queue's UI timer: copies the latest engine snapshot into bindable properties.</summary>
    public void RefreshProgress()
    {
        if (latest is not { } p) return;
        CurrentFile = p.CurrentFile;
        FilePercent = p.FileBytesTotal > 0 ? 100.0 * p.FileBytesDone / p.FileBytesTotal : 100;
        TotalPercent = p.BytesTotal > 0 ? 100.0 * p.BytesDone / p.BytesTotal : (p.FilesTotal > 0 ? 100.0 * p.FilesDone / p.FilesTotal : 0);
        if (State is OperationState.Running or OperationState.Paused)
        {
            var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            var speed = p.BytesDone / seconds;
            var remaining = speed > 0 ? TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / speed) : TimeSpan.Zero;
            DetailText = $"{p.FilesDone} / {p.FilesTotal} files · {SizeFormatter.Human(p.BytesDone)} / {SizeFormatter.Human(p.BytesTotal)} · " +
                         $"{SizeFormatter.Human((long)speed)}/s · {remaining:hh\\:mm\\:ss} left";
        }
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (State == OperationState.Running)
        {
            pause.Pause();
            clock.Stop();
            State = OperationState.Paused;
        }
        else if (State == OperationState.Paused)
        {
            pause.Resume();
            clock.Start();
            State = OperationState.Running;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (State == OperationState.Queued)
        {
            State = OperationState.Cancelled;
            DetailText = "Cancelled";
            OnPropertyChanged(nameof(IsFinished));
            return;
        }
        pause.Resume();
        cts.Cancel();
    }

    // ---- ITransferCallbacks (called from the worker thread) ----------------------------------

    void ITransferCallbacks.Report(TransferProgress progress) => latest = progress;

    Task<ConflictChoice> ITransferCallbacks.ResolveConflictAsync(ConflictInfo conflict) =>
        Application.Current.Dispatcher.InvokeAsync(() => dialogs.ResolveConflict(conflict)).Task;

    Task<ErrorChoice> ITransferCallbacks.HandleErrorAsync(string path, Exception error) =>
        Application.Current.Dispatcher.InvokeAsync(() => dialogs.AskOnError(path, error.Message)).Task;
}

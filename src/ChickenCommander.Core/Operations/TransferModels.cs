namespace ChickenCommander.Core.Operations;

public enum TransferKind { Copy, Move }

/// <summary>What to do when a target file already exists, unless the user decides case by case.</summary>
public enum OverwriteMode { Ask, OverwriteAll, SkipAll, OverwriteOlder, RenameAll }

public enum ConflictChoice { Overwrite, Skip, Rename, Cancel, OverwriteAll, SkipAll, OverwriteOlderAll, RenameAll }

public enum ErrorChoice { Retry, Skip, Cancel }

public sealed record TransferRequest(
    TransferKind Kind,
    IReadOnlyList<string> Sources,
    string Target,
    OverwriteMode Overwrite = OverwriteMode.Ask);

public sealed record ConflictInfo(
    string Source,
    string Destination,
    long SourceSize,
    DateTime SourceModified,
    long DestinationSize,
    DateTime DestinationModified);

public sealed record TransferProgress(
    string CurrentFile,
    long FileBytesDone,
    long FileBytesTotal,
    long BytesDone,
    long BytesTotal,
    int FilesDone,
    int FilesTotal);

public sealed record TransferResult(int FilesDone, int Skipped, int Errors, bool Cancelled);

/// <summary>How the engine talks back to the UI. Calls may come from any thread.</summary>
public interface ITransferCallbacks
{
    Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict);

    Task<ErrorChoice> HandleErrorAsync(string path, Exception error);

    void Report(TransferProgress progress);
}

/// <summary>Lets the UI pause a running operation between buffer writes.</summary>
public sealed class PauseController
{
    private volatile TaskCompletionSource? gate;

    public bool IsPaused => gate is not null;

    public void Pause() => gate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Resume()
    {
        var g = gate;
        gate = null;
        g?.TrySetResult();
    }

    public Task WaitIfPausedAsync(CancellationToken ct) => gate is { } g ? g.Task.WaitAsync(ct) : Task.CompletedTask;
}

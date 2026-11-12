using System.Collections.ObjectModel;
using System.Windows.Threading;
using ChickenCommander.ViewModels;

namespace ChickenCommander.Services;

/// <summary>
/// Runs copy/move jobs one after another in the background, so the panels stay usable.
/// A UI timer pushes progress snapshots to the visible job a few times per second.
/// </summary>
public sealed class OperationQueue
{
    private readonly DispatcherTimer timer;
    private bool running;

    public OperationQueue()
    {
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            foreach (var op in Items.Where(o => !o.IsFinished)) op.RefreshProgress();
        };
    }

    public ObservableCollection<TransferOperationViewModel> Items { get; } = [];

    public bool HasActiveWork => Items.Any(o => !o.IsFinished);

    /// <summary>Raised on the UI thread when a job ends (done, cancelled or failed).</summary>
    public event Action<TransferOperationViewModel>? Completed;

    /// <summary>Raised when the first job of an idle queue starts, so the window can show itself.</summary>
    public event Action? Started;

    public void Enqueue(TransferOperationViewModel operation)
    {
        // Drop finished jobs from the list when new work arrives.
        foreach (var done in Items.Where(o => o.IsFinished).ToList()) Items.Remove(done);
        Items.Add(operation);
        if (!running) _ = RunLoopAsync();
    }

    private async Task RunLoopAsync()
    {
        running = true;
        timer.Start();
        Started?.Invoke();
        try
        {
            while (Items.FirstOrDefault(o => o.State == OperationState.Queued) is { } next)
            {
                await next.RunAsync();
                Completed?.Invoke(next);
            }
        }
        finally
        {
            timer.Stop();
            running = false;
        }
    }
}

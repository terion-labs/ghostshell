namespace GhostShell.Desktop;

/// <summary>Best-effort sampled protection for an owned renderer, not a kernel memory guarantee.</summary>
internal sealed class DiagramMemoryWatchdog : IAsyncDisposable
{
    internal const long MaximumWorkingSetBytes = 2L * 1024 * 1024 * 1024;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _monitor;
    public bool Exceeded { get; private set; }

    public DiagramMemoryWatchdog(Func<long> sampleWorkingSet, Action terminateOwnedWorker, TimeSpan? interval = null)
    {
        _monitor = MonitorAsync(sampleWorkingSet, terminateOwnedWorker, interval ?? TimeSpan.FromSeconds(1));
    }

    private async Task MonitorAsync(Func<long> sample, Action terminate, TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (sample() > MaximumWorkingSetBytes)
                {
                    Exceeded = true;
                    terminate();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException)
        {
            // The owned process exited between the sample and termination.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _monitor.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

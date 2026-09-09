using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Asura.AccessibilityAcceptance;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ProcessIdentity(int ProcessId, ulong StartToken);

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TrackedProcess(ProcessIdentity Identity, int Depth);

internal sealed record ProcessTreeInspection(
    IReadOnlyList<TrackedProcess> LiveProcesses,
    int CapturedCount)
{
    public bool AllCapturedExited => LiveProcesses.Count == 0;
}

internal sealed record ProcessTreeCleanupResult(
    bool AllCapturedExited,
    bool TerminationAttempted,
    int CapturedCount);

internal interface IProcessTreeProbe
{
    ProcessIdentity? ReadIdentity(int processId);

    IReadOnlyList<TrackedProcess> CaptureDescendants(ProcessIdentity rootIdentity);

    void TerminateExact(ProcessIdentity identity);
}

/// <summary>
/// Retains the stable identities of the package process and every descendant observed while
/// that parent is live. PID-only cleanup is deliberately forbidden because a recycled PID can
/// belong to an unrelated process by the time the acceptance run ends.
/// </summary>
internal sealed class ProcessTreeTracker : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SamplerJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _sync = new();
    private readonly IProcessTreeProbe _probe;
    private readonly Dictionary<ProcessIdentity, int> _captured = [];
    private CancellationTokenSource? _samplingCancellation;
    private Thread? _samplingThread;
    private Exception? _samplingFailure;

    private ProcessTreeTracker(ProcessIdentity rootIdentity, IProcessTreeProbe probe)
    {
        RootIdentity = rootIdentity;
        _probe = probe;
        _captured.Add(rootIdentity, 0);
    }

    public ProcessIdentity RootIdentity { get; }

    public int CapturedCount
    {
        get
        {
            lock (_sync)
            {
                return _captured.Count;
            }
        }
    }

    public static ProcessTreeTracker Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var probe = ProcessTreeProbe.CreateForCurrentPlatform();
        var identity = probe.ReadIdentity(process.Id)
            ?? throw new ProcessTreeProbeException(
                "The packaged process exited before its stable identity could be captured.");
        var tracker = new ProcessTreeTracker(identity, probe);
        tracker.StartSampling(PollInterval);
        return tracker;
    }

    internal static ProcessTreeTracker CreateForTests(
        ProcessIdentity rootIdentity,
        IProcessTreeProbe probe) =>
        new(rootIdentity, probe);

    internal void StartSamplingForTests(TimeSpan interval) => StartSampling(interval);

    public void CaptureSnapshot()
    {
        lock (_sync)
        {
            ThrowIfSamplingFailed();
            CaptureSnapshotCore();
            ThrowIfSamplingFailed();
        }
    }

    public ProcessTreeInspection Inspect()
    {
        lock (_sync)
        {
            ThrowIfSamplingFailed();
            var live = new List<TrackedProcess>(_captured.Count);
            foreach (var captured in _captured)
            {
                var current = _probe.ReadIdentity(captured.Key.ProcessId);
                if (current == captured.Key)
                {
                    live.Add(new TrackedProcess(captured.Key, captured.Value));
                }
            }

            return new ProcessTreeInspection(live, _captured.Count);
        }
    }

    public bool WaitForAllExited(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (Inspect().AllCapturedExited)
            {
                return true;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            Thread.Sleep(remaining < PollInterval ? remaining : PollInterval);
        }
    }

    public ProcessTreeCleanupResult TerminateAndWait(TimeSpan timeout)
    {
        StopSampling();
        lock (_sync)
        {
            // Take one last snapshot before stopping the root. This captures late children while
            // the package still owns them; retained identities continue to cover detached children.
            CaptureSnapshot();
            var live = Inspect().LiveProcesses;
            if (live.Count == 0)
            {
                return new ProcessTreeCleanupResult(true, false, _captured.Count);
            }

            foreach (var process in live
                         .OrderBy(item => item.Depth == 0 ? int.MinValue : -item.Depth))
            {
                _probe.TerminateExact(process.Identity);
            }

            return new ProcessTreeCleanupResult(
                WaitForAllExited(timeout),
                true,
                _captured.Count);
        }
    }

    public void StopSampling()
    {
        Thread? thread;
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            thread = _samplingThread;
            _samplingCancellation?.Cancel();
        }

        if (thread is not null
            && thread != Thread.CurrentThread
            && !thread.Join(SamplerJoinTimeout))
        {
            throw new ProcessTreeProbeException(
                "The background package process-tree sampler did not stop in time.");
        }

        try
        {
            lock (_sync)
            {
                if (_samplingThread == thread)
                {
                    _samplingThread = null;
                }

                cancellation = _samplingCancellation;
                _samplingCancellation = null;

                ThrowIfSamplingFailed();
            }
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    public void Dispose() => StopSampling();

    private void StartSampling(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        lock (_sync)
        {
            if (_samplingThread is not null)
            {
                throw new InvalidOperationException(
                    "Package process-tree sampling has already started.");
            }

            _samplingCancellation = new CancellationTokenSource();
            _samplingThread = new Thread(() => SampleUntilRootExits(
                interval,
                _samplingCancellation.Token))
            {
                IsBackground = true,
                Name = "Asura acceptance process-tree sampler",
            };
            _samplingThread.Start();
        }
    }

    private void SampleUntilRootExits(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                lock (_sync)
                {
                    CaptureSnapshotCore();
                    if (_probe.ReadIdentity(RootIdentity.ProcessId) != RootIdentity)
                    {
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _samplingFailure ??= exception;
                }

                return;
            }

            if (cancellationToken.WaitHandle.WaitOne(interval))
            {
                return;
            }
        }
    }

    private void CaptureSnapshotCore()
    {
        foreach (var process in _probe.CaptureDescendants(RootIdentity))
        {
            if (_captured.TryGetValue(process.Identity, out var existingDepth))
            {
                _captured[process.Identity] = Math.Max(existingDepth, process.Depth);
                continue;
            }

            _captured.Add(process.Identity, process.Depth);
        }
    }

    private void ThrowIfSamplingFailed()
    {
        if (_samplingFailure is not null)
        {
            throw new ProcessTreeProbeException(
                "Background package process-tree sampling failed.",
                _samplingFailure);
        }
    }
}

internal sealed class ProcessTreeProbeException : Exception
{
    public ProcessTreeProbeException(string message)
        : base(message)
    {
    }

    public ProcessTreeProbeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

}

using System.Diagnostics;

namespace Asura.AccessibilityAcceptance.Tests;

public sealed class ProcessTreeTrackerTests
{
    [Fact]
    public void Descendant_graph_records_depth_and_rejects_parent_cycles()
    {
        ProcessParent[] processes =
        [
            new(10, 1),
            new(11, 10),
            new(12, 11),
            new(20, 1),
        ];

        Assert.Equal(
            [(11, 1), (12, 2)],
            ProcessTreeProbe.FindDescendants(10, processes));

        ProcessParent[] cycle =
        [
            new(10, 12),
            new(11, 10),
            new(12, 11),
        ];
        Assert.Throws<ProcessTreeProbeException>(() =>
            ProcessTreeProbe.FindDescendants(10, cycle));
    }

    [Fact]
    public void Reused_pids_are_not_live_and_are_never_terminated()
    {
        var root = new ProcessIdentity(10, 100);
        var child = new ProcessIdentity(11, 110);
        var probe = new FakeProcessTreeProbe(root, child);
        var tracker = ProcessTreeTracker.CreateForTests(root, probe);
        tracker.CaptureSnapshot();

        probe.ReplaceIdentity(new ProcessIdentity(10, 900));
        probe.ReplaceIdentity(new ProcessIdentity(11, 910));

        var result = tracker.TerminateAndWait(TimeSpan.Zero);

        Assert.True(result.AllCapturedExited);
        Assert.False(result.TerminationAttempted);
        Assert.Empty(probe.Terminated);
    }

    [Fact]
    public void Captured_detached_descendant_remains_owned_after_parent_exit()
    {
        var root = new ProcessIdentity(20, 200);
        var child = new ProcessIdentity(21, 210);
        var probe = new FakeProcessTreeProbe(root, child);
        var tracker = ProcessTreeTracker.CreateForTests(root, probe);
        tracker.CaptureSnapshot();
        probe.RemoveIdentity(root.ProcessId);

        Assert.Equal([child], tracker.Inspect().LiveProcesses.Select(
            process => process.Identity));

        var result = tracker.TerminateAndWait(TimeSpan.Zero);

        Assert.True(result.AllCapturedExited);
        Assert.True(result.TerminationAttempted);
        Assert.Equal([child], probe.Terminated);
    }

    [Fact]
    public void Background_sampler_retains_a_grandchild_after_transient_parent_exits()
    {
        var root = new ProcessIdentity(30, 300);
        var transientParent = new ProcessIdentity(31, 310);
        var detachedGrandchild = new ProcessIdentity(32, 320);
        var probe = new FakeProcessTreeProbe(root, transientParent);
        probe.AddIdentity(detachedGrandchild);
        probe.SetDescendants(
            new TrackedProcess(transientParent, 1),
            new TrackedProcess(detachedGrandchild, 2));
        var tracker = ProcessTreeTracker.CreateForTests(root, probe);
        tracker.StartSamplingForTests(TimeSpan.FromMilliseconds(1));
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => tracker.CapturedCount == 3,
                TimeSpan.FromSeconds(2)));

            probe.SetDescendants();
            probe.RemoveIdentity(root.ProcessId);
            probe.RemoveIdentity(transientParent.ProcessId);
            tracker.StopSampling();

            Assert.Equal(
                [detachedGrandchild],
                tracker.Inspect().LiveProcesses.Select(process => process.Identity));
            Assert.True(tracker.TerminateAndWait(TimeSpan.Zero).AllCapturedExited);
            Assert.Equal([detachedGrandchild], probe.Terminated);
        }
        finally
        {
            tracker.StopSampling();
        }
    }

    [Fact]
    public void Background_sampler_error_is_retained_and_fails_closed()
    {
        var root = new ProcessIdentity(40, 400);
        var child = new ProcessIdentity(41, 410);
        var probe = new FakeProcessTreeProbe(root, child);
        var tracker = ProcessTreeTracker.CreateForTests(root, probe);
        probe.FailCapture();
        tracker.StartSamplingForTests(TimeSpan.FromMilliseconds(1));

        Assert.True(SpinWait.SpinUntil(
            () => probe.CaptureCallCount > 0,
            TimeSpan.FromSeconds(2)));
        var exception = Assert.Throws<ProcessTreeProbeException>(tracker.StopSampling);

        Assert.Contains("Background package process-tree sampling failed", exception.Message);
        Assert.IsType<ProcessTreeProbeException>(exception.InnerException);
        Assert.Empty(probe.Terminated);
    }

    [Fact]
    public void Windows_snapshot_rejects_stale_and_reused_parent_edges()
    {
        var root = new ProcessIdentity(100, 500);
        ProcessParent[] parents =
        [
            new(100, 1),
            new(200, 100),
            new(201, 200),
            new(300, 100),
            new(301, 300),
            new(400, 100),
            new(401, 400),
            new(500, 100),
        ];
        var identities = new Dictionary<int, ProcessIdentity>
        {
            // This process predates the reused package PID, so its apparent PPID is stale.
            [200] = new(200, 400),
            [201] = new(201, 450),
            // This edge is a valid package ancestry chain.
            [300] = new(300, 600),
            [301] = new(301, 700),
            // The child predates its reused numeric parent and must also be rejected.
            [400] = new(400, 800),
            [401] = new(401, 750),
            // This PID was reused after Toolhelp froze the stale numeric record.
            [500] = new(500, 1_100),
        };

        var selected = WindowsProcessTreeProbe.SelectStableDescendants(
            root,
            parents,
            identityMustExistBy: 1_000,
            identities);

        Assert.Equal(
            [identities[300], identities[400], identities[301]],
            selected.Select(process => process.Identity));
    }

    [Fact]
    public void Current_platform_tracks_and_terminates_a_runner_owned_tree()
    {
        using var process = StartOwnedProcessTree();
        ProcessTreeTracker? tracker = null;
        try
        {
            tracker = ProcessTreeTracker.Attach(process);
            for (var attempt = 0; attempt < 40 && tracker.CapturedCount < 2; attempt++)
            {
                tracker.CaptureSnapshot();
                Thread.Sleep(50);
            }

            Assert.True(tracker.CapturedCount >= 2);

            if (OperatingSystem.IsMacOS())
            {
                var exception = Assert.Throws<ProcessTreeProbeException>(() =>
                    tracker.TerminateAndWait(TimeSpan.FromSeconds(1)));
                Assert.Contains("manual package cleanup", exception.Message);
                return;
            }

            var result = tracker.TerminateAndWait(TimeSpan.FromSeconds(5));

            Assert.True(result.TerminationAttempted);
            Assert.True(result.AllCapturedExited);
            Assert.True(process.WaitForExit(milliseconds: 5_000));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 5_000);
            }
        }
    }

    private static Process StartOwnedProcessTree()
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("ping -n 31 127.0.0.1 > nul");
        }
        else
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("sleep 30 & wait");
        }

        return Process.Start(start)
            ?? throw new InvalidOperationException("The owned process tree did not start.");
    }

    private sealed class FakeProcessTreeProbe : IProcessTreeProbe
    {
        private readonly object _sync = new();
        private readonly ProcessIdentity _root;
        private readonly ProcessIdentity _child;
        private readonly Dictionary<int, ProcessIdentity> _current;
        private TrackedProcess[] _descendants;
        private bool _failCapture;
        private int _captureCallCount;

        public FakeProcessTreeProbe(ProcessIdentity root, ProcessIdentity child)
        {
            _root = root;
            _child = child;
            _current = new Dictionary<int, ProcessIdentity>
            {
                [root.ProcessId] = root,
                [child.ProcessId] = child,
            };
            _descendants = [new TrackedProcess(child, 1)];
        }

        public List<ProcessIdentity> Terminated { get; } = [];

        public int CaptureCallCount
        {
            get
            {
                lock (_sync)
                {
                    return _captureCallCount;
                }
            }
        }

        public ProcessIdentity? ReadIdentity(int processId)
        {
            lock (_sync)
            {
                return _current.GetValueOrDefault(processId);
            }
        }

        public IReadOnlyList<TrackedProcess> CaptureDescendants(
            ProcessIdentity rootIdentity)
        {
            lock (_sync)
            {
                _captureCallCount++;
                if (_failCapture)
                {
                    throw new ProcessTreeProbeException("Synthetic capture failure.");
                }

                return _current.GetValueOrDefault(_root.ProcessId) == rootIdentity
                    ? _descendants.ToArray()
                    : [];
            }
        }

        public void TerminateExact(ProcessIdentity identity)
        {
            lock (_sync)
            {
                if (_current.GetValueOrDefault(identity.ProcessId) != identity)
                {
                    return;
                }

                Terminated.Add(identity);
                _current.Remove(identity.ProcessId);
            }
        }

        public void ReplaceIdentity(ProcessIdentity identity)
        {
            lock (_sync)
            {
                _current[identity.ProcessId] = identity;
            }
        }

        public void AddIdentity(ProcessIdentity identity) => ReplaceIdentity(identity);

        public void FailCapture()
        {
            lock (_sync)
            {
                _failCapture = true;
            }
        }

        public void RemoveIdentity(int processId)
        {
            lock (_sync)
            {
                _current.Remove(processId);
            }
        }

        public void SetDescendants(params TrackedProcess[] descendants)
        {
            lock (_sync)
            {
                _descendants = [.. descendants];
            }
        }
    }
}

using System.Runtime.InteropServices;

namespace Asura.AccessibilityAcceptance;

internal sealed class MacProcessTreeProbe : IProcessTreeProbe
{
    private const int NoPermission = 1;
    private const int NoSuchProcess = 3;
    private const int ProcessBsdInfoFlavor = 3;

    public ProcessIdentity? ReadIdentity(int processId) =>
        ReadProcess(processId)?.Identity;

    public IReadOnlyList<TrackedProcess> CaptureDescendants(
        ProcessIdentity rootIdentity)
    {
        var root = ReadProcess(rootIdentity.ProcessId);
        if (root is null || root.Value.Identity != rootIdentity)
        {
            return [];
        }

        var descendants = new List<TrackedProcess>();
        var visited = new HashSet<int> { rootIdentity.ProcessId };
        var pending = new Queue<(MacProcess Process, int Depth)>();
        pending.Enqueue((root.Value, 0));
        while (pending.TryDequeue(out var parent))
        {
            if (ReadIdentity(parent.Process.Identity.ProcessId)
                != parent.Process.Identity)
            {
                continue;
            }

            var childProcessIds = ListChildProcessIds(
                parent.Process.Identity.ProcessId);
            if (ReadIdentity(parent.Process.Identity.ProcessId)
                != parent.Process.Identity)
            {
                continue;
            }

            var directChildren = new List<MacProcess>();
            var uniqueChildProcessIds = new HashSet<int>();
            foreach (var childProcessId in childProcessIds)
            {
                if (!uniqueChildProcessIds.Add(childProcessId))
                {
                    throw new ProcessTreeProbeException(
                        "macOS returned a duplicate child process identity.");
                }

                var child = ReadProcess(childProcessId);
                if (child is null)
                {
                    continue;
                }

                // The child-list result and BSD identity read are separate kernel calls. Require
                // the relationship still to be current and temporally possible so a raced PID
                // reuse is skipped rather than retained for cleanup.
                if (child.Value.ParentProcessId != parent.Process.Identity.ProcessId
                    || child.Value.Identity.StartToken < parent.Process.Identity.StartToken)
                {
                    continue;
                }

                directChildren.Add(child.Value);
            }

            if (ReadIdentity(parent.Process.Identity.ProcessId)
                != parent.Process.Identity)
            {
                continue;
            }

            foreach (var child in directChildren)
            {
                if (!visited.Add(child.Identity.ProcessId))
                {
                    throw new ProcessTreeProbeException(
                        "macOS returned a cyclic child process identity.");
                }

                var depth = parent.Depth + 1;
                descendants.Add(new TrackedProcess(child.Identity, depth));
                pending.Enqueue((child, depth));
            }
        }

        return descendants;
    }

    public void TerminateExact(ProcessIdentity identity)
    {
        if (ReadIdentity(identity.ProcessId) != identity)
        {
            return;
        }

        // Darwin exposes no public identity-bound process handle equivalent to Linux pidfd or
        // the Windows process handle. A PID can be recycled between proc_pidinfo and kill(2), so
        // automatic signaling would risk terminating an unrelated process. Normal acceptance
        // still passes when the operator closes every captured identity; otherwise fail closed.
        throw new ProcessTreeProbeException(
            "macOS cannot terminate a tracked PID without an identity race; manual package cleanup is required.");
    }

    private static int[] ListChildProcessIds(int parentProcessId)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var count = ProcListChildPids(parentProcessId, null, 0);
            if (count < 0)
            {
                throw new ProcessTreeProbeException(
                    $"macOS child-process enumeration failed for PID {parentProcessId}.");
            }

            if (count == 0)
            {
                return [];
            }

            var processIds = new int[Math.Max(256, count * 2)];
            var actual = ProcListChildPids(
                parentProcessId,
                processIds,
                checked(processIds.Length * sizeof(int)));
            if (actual < 0)
            {
                throw new ProcessTreeProbeException(
                    "macOS process inventory enumeration failed.");
            }

            if (actual < processIds.Length)
            {
                return processIds[..actual];
            }
        }

        throw new ProcessTreeProbeException(
            "The macOS child-process inventory changed too quickly to capture safely.");
    }

    private static MacProcess? ReadProcess(int processId)
    {
        var info = new ProcBsdInfo();
        var expectedSize = Marshal.SizeOf<ProcBsdInfo>();
        var actualSize = ProcPidInfo(
            processId,
            ProcessBsdInfoFlavor,
            0,
            ref info,
            expectedSize);
        if (actualSize == expectedSize)
        {
            if (info.ProcessId != (uint)processId)
            {
                throw new ProcessTreeProbeException(
                    "macOS returned a mismatched process identity.");
            }

            var startToken = checked(
                (info.StartSeconds * 1_000_000UL) + info.StartMicroseconds);
            return new MacProcess(
                new ProcessIdentity(processId, startToken),
                checked((int)info.ParentProcessId));
        }

        if (actualSize != 0)
        {
            throw new ProcessTreeProbeException(
                $"macOS returned an incomplete process record for PID {processId}.");
        }

        if (NativeKill(processId, 0) == 0)
        {
            throw new ProcessTreeProbeException(
                $"macOS could not inspect the live process with PID {processId}.");
        }

        var error = Marshal.GetLastPInvokeError();
        return error switch
        {
            NoSuchProcess => null,
            NoPermission => throw new ProcessTreeProbeException(
                $"macOS denied identity access to the live process with PID {processId}."),
            _ => throw new ProcessTreeProbeException(
                $"macOS process identity probing failed with native error {error}."),
        };
    }

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listchildpids")]
    private static extern int ProcListChildPids(
        int parentProcessId,
        [Out] int[]? buffer,
        int bufferSize);

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pidinfo")]
    private static extern int ProcPidInfo(
        int processId,
        int flavor,
        ulong argument,
        ref ProcBsdInfo buffer,
        int bufferSize);

    [DllImport(
        "/usr/lib/libSystem.B.dylib",
        EntryPoint = "kill",
        SetLastError = true)]
    private static extern int NativeKill(int processId, int signal);

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct ProcBsdInfo
    {
        [FieldOffset(12)]
        public uint ProcessId;

        [FieldOffset(16)]
        public uint ParentProcessId;

        [FieldOffset(120)]
        public ulong StartSeconds;

        [FieldOffset(128)]
        public ulong StartMicroseconds;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct MacProcess(
        ProcessIdentity Identity,
        int ParentProcessId);
}

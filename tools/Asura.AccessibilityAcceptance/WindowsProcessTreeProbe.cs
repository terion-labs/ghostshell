using System.Runtime.InteropServices;

namespace Asura.AccessibilityAcceptance;

internal sealed class WindowsProcessTreeProbe : IProcessTreeProbe
{
    private const uint CreateProcessSnapshot = 0x00000002;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int AccessDenied = 5;
    private const int BadLength = 24;
    private const int InvalidParameter = 87;
    private const int NoMoreFiles = 18;
    private static readonly IntPtr InvalidHandle = new(-1);

    public ProcessIdentity? ReadIdentity(int processId)
    {
        var handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == InvalidParameter)
            {
                return null;
            }

            throw new ProcessTreeProbeException(error == AccessDenied
                ? $"Windows denied identity access to the live process with PID {processId}."
                : $"Windows process identity probing failed with native error {error}.");
        }

        try
        {
            return new ProcessIdentity(processId, ReadStartToken(handle));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public IReadOnlyList<TrackedProcess> CaptureDescendants(
        ProcessIdentity rootIdentity)
    {
        if (ReadIdentity(rootIdentity.ProcessId) != rootIdentity)
        {
            return [];
        }

        var snapshot = CaptureParents();
        var descendants = ProcessTreeProbe.FindDescendants(
            rootIdentity.ProcessId,
            snapshot.Parents);
        var identities = new Dictionary<int, ProcessIdentity>();
        foreach (var descendant in descendants)
        {
            var identity = ReadIdentity(descendant.ProcessId);
            if (identity is not null)
            {
                identities.Add(descendant.ProcessId, identity.Value);
            }
        }

        return ReadIdentity(rootIdentity.ProcessId) == rootIdentity
            ? SelectStableDescendants(
                rootIdentity,
                snapshot.Parents,
                snapshot.IdentityMustExistBy,
                identities)
            : [];
    }

    internal static IReadOnlyList<TrackedProcess> SelectStableDescendants(
        ProcessIdentity rootIdentity,
        IReadOnlyCollection<ProcessParent> parents,
        ulong identityMustExistBy,
        IReadOnlyDictionary<int, ProcessIdentity> identities)
    {
        var parentByProcessId = parents.ToDictionary(
            process => process.ProcessId,
            process => process.ParentProcessId);
        var trusted = new Dictionary<int, ProcessIdentity>
        {
            [rootIdentity.ProcessId] = rootIdentity,
        };
        var stable = new List<TrackedProcess>();
        foreach (var descendant in ProcessTreeProbe.FindDescendants(
                     rootIdentity.ProcessId,
                     parents))
        {
            var parentProcessId = parentByProcessId[descendant.ProcessId];
            if (!trusted.TryGetValue(parentProcessId, out var parentIdentity)
                || !identities.TryGetValue(descendant.ProcessId, out var identity))
            {
                continue;
            }

            // Toolhelp preserves only a numeric PPID. If that PID has been reused, an old,
            // unrelated process can appear to be a child of the package. Creation order makes
            // every accepted ancestry edge stable. Requiring the identity to predate the frozen
            // Toolhelp snapshot also rejects a PID reused while its stale entry is enumerated.
            // The background sampler captures legitimate children born after this boundary.
            if (identity.StartToken < parentIdentity.StartToken
                || identity.StartToken > identityMustExistBy)
            {
                continue;
            }

            trusted.Add(identity.ProcessId, identity);
            stable.Add(new TrackedProcess(identity, descendant.Depth));
        }

        return stable;
    }

    public void TerminateExact(ProcessIdentity identity)
    {
        var handle = OpenProcess(
            ProcessTerminate | ProcessQueryLimitedInformation,
            inheritHandle: false,
            identity.ProcessId);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == InvalidParameter)
            {
                return;
            }

            throw new ProcessTreeProbeException(error == AccessDenied
                ? "Windows denied exact process cleanup; manual cleanup is required."
                : $"Windows process cleanup failed with native error {error}.");
        }

        try
        {
            if (ReadStartToken(handle) != identity.StartToken)
            {
                return;
            }

            if (!TerminateProcess(handle, 1))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != InvalidParameter)
                {
                    throw new ProcessTreeProbeException(
                        $"Windows process termination failed with native error {error}.");
                }
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static WindowsProcessSnapshot CaptureParents()
    {
        IntPtr snapshot = IntPtr.Zero;
        ulong identityMustExistBy = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            GetSystemTimeAsFileTime(out var snapshotBoundary);
            identityMustExistBy = snapshotBoundary.ToUInt64();
            snapshot = CreateToolhelp32Snapshot(CreateProcessSnapshot, 0);
            if (snapshot != InvalidHandle)
            {
                break;
            }

            if (Marshal.GetLastPInvokeError() != BadLength)
            {
                break;
            }
        }

        if (snapshot == InvalidHandle || snapshot == IntPtr.Zero)
        {
            throw new ProcessTreeProbeException(
                $"Windows process inventory failed with native error {Marshal.GetLastPInvokeError()}.");
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = checked((uint)Marshal.SizeOf<ProcessEntry32>()),
            };
            if (!Process32First(snapshot, ref entry))
            {
                var error = Marshal.GetLastPInvokeError();
                return error == NoMoreFiles
                    ? new WindowsProcessSnapshot([], identityMustExistBy)
                    : throw new ProcessTreeProbeException(
                        $"Windows process inventory failed with native error {error}.");
            }

            var processes = new List<ProcessParent>();
            do
            {
                if (entry.ProcessId > 0
                    && entry.ProcessId <= int.MaxValue
                    && entry.ParentProcessId <= int.MaxValue)
                {
                    var processId = (int)entry.ProcessId;
                    processes.Add(new ProcessParent(processId, (int)entry.ParentProcessId));
                }

                entry.Size = checked((uint)Marshal.SizeOf<ProcessEntry32>());
            }
            while (Process32Next(snapshot, ref entry));

            var finalError = Marshal.GetLastPInvokeError();
            if (finalError != NoMoreFiles)
            {
                throw new ProcessTreeProbeException(
                    $"Windows process inventory ended with native error {finalError}.");
            }

            return new WindowsProcessSnapshot(processes, identityMustExistBy);
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static ulong ReadStartToken(IntPtr processHandle)
    {
        if (!GetProcessTimes(
                processHandle,
                out var creationTime,
                out _,
                out _,
                out _))
        {
            throw new ProcessTreeProbeException(
                $"Windows process timing failed with native error {Marshal.GetLastPInvokeError()}.");
        }

        return creationTime.ToUInt64();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "Process32FirstW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        IntPtr snapshot,
        ref ProcessEntry32 entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "Process32NextW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        IntPtr snapshot,
        ref ProcessEntry32 entry);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(
        IntPtr processHandle,
        out NativeFileTime creationTime,
        out NativeFileTime exitTime,
        out NativeFileTime kernelTime,
        out NativeFileTime userTime);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr processHandle, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern void GetSystemTimeAsFileTime(out NativeFileTime systemTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint _low;
        private readonly uint _high;

        public ulong ToUInt64() => ((ulong)_high << 32) | _low;
    }

    private sealed record WindowsProcessSnapshot(
        IReadOnlyList<ProcessParent> Parents,
        ulong IdentityMustExistBy);
}

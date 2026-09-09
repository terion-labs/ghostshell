using System.Globalization;
using System.Runtime.InteropServices;

namespace Asura.AccessibilityAcceptance;

internal sealed class LinuxProcessTreeProbe : IProcessTreeProbe
{
    private const int NoSuchProcess = 3;
    private const int NoSystemCall = 38;
    private const int SignalKill = 9;
    private const long PidFdOpenSystemCall = 434;
    private const long PidFdSendSignalSystemCall = 424;

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
        var pending = new Queue<(LinuxProcess Process, int Depth)>();
        pending.Enqueue((root.Value, 0));
        while (pending.TryDequeue(out var parent))
        {
            if (ReadIdentity(parent.Process.Identity.ProcessId)
                != parent.Process.Identity)
            {
                continue;
            }

            var childReferences = ListChildProcesses(
                parent.Process.Identity.ProcessId);
            if (ReadIdentity(parent.Process.Identity.ProcessId)
                != parent.Process.Identity)
            {
                continue;
            }

            var directChildren = new List<LinuxProcess>();
            var childProcessIds = new HashSet<int>();
            foreach (var childReference in childReferences)
            {
                if (!childProcessIds.Add(childReference.ProcessId))
                {
                    throw new ProcessTreeProbeException(
                        "Linux returned a duplicate child process identity.");
                }

                var child = ReadProcess(childReference.ProcessId);
                if (child is null)
                {
                    continue;
                }

                // The task/children files cover children created by every thread, while stat
                // reports the parent thread-group leader as PPID. Match that current process
                // edge and monotonic start token so a raced PID reuse is never retained.
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
                        "Linux returned a cyclic child process identity.");
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
        if (RuntimeInformation.ProcessArchitecture is not Architecture.X64
            and not Architecture.Arm64)
        {
            throw new ProcessTreeProbeException(
                "Exact Linux cleanup requires pidfd support on x64 or arm64.");
        }

        var descriptor = SyscallPidFdOpen(
            PidFdOpenSystemCall,
            identity.ProcessId,
            0);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NoSuchProcess)
            {
                return;
            }

            throw new ProcessTreeProbeException(error == NoSystemCall
                ? "The Linux kernel does not expose pidfd cleanup; manual cleanup is required."
                : $"Linux pidfd_open failed with native error {error}.");
        }

        try
        {
            if (ReadIdentity(identity.ProcessId) != identity)
            {
                return;
            }

            if (SyscallPidFdSendSignal(
                    PidFdSendSignalSystemCall,
                    (int)descriptor,
                    SignalKill,
                    IntPtr.Zero,
                    0) < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != NoSuchProcess)
                {
                    throw new ProcessTreeProbeException(
                        $"Linux pidfd_send_signal failed with native error {error}.");
                }
            }
        }
        finally
        {
            _ = Close((int)descriptor);
        }
    }

    private static IReadOnlyList<LinuxChildReference> ListChildProcesses(
        int parentProcessId)
    {
        var processDirectory = Path.Combine("/proc", parentProcessId.ToString(
            CultureInfo.InvariantCulture));
        var taskDirectory = Path.Combine(processDirectory, "task");
        string[] threadDirectories;
        try
        {
            threadDirectories = Directory.GetDirectories(taskDirectory);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            throw new ProcessTreeProbeException(
                $"Linux child-process enumeration failed for PID {parentProcessId}.",
                exception);
        }

        var children = new List<LinuxChildReference>();
        foreach (var threadDirectory in threadDirectories)
        {
            if (!int.TryParse(
                    Path.GetFileName(threadDirectory),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var threadId)
                || threadId <= 0)
            {
                continue;
            }

            string childList;
            try
            {
                childList = File.ReadAllText(Path.Combine(threadDirectory, "children"));
            }
            catch (Exception exception) when (exception is IOException
                && (!Directory.Exists(threadDirectory)
                || !Directory.Exists(processDirectory))
            )
            {
                continue;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                throw new ProcessTreeProbeException(
                    $"Linux child-process enumeration failed for task {threadId}.",
                    exception);
            }

            foreach (var value in childList.Split(
                         (char[]?)null,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var childProcessId)
                    || childProcessId <= 0)
                {
                    throw new ProcessTreeProbeException(
                        $"Linux returned a malformed child PID for task {threadId}.");
                }

                children.Add(new LinuxChildReference(childProcessId));
            }
        }

        return children;
    }

    private static LinuxProcess? ReadProcess(int processId)
    {
        var processDirectory = Path.Combine("/proc", processId.ToString(
            CultureInfo.InvariantCulture));
        var statPath = Path.Combine(processDirectory, "stat");
        string stat;
        try
        {
            stat = File.ReadAllText(statPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException) when (!Directory.Exists(processDirectory))
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            throw new ProcessTreeProbeException(
                $"Linux process identity probing failed for PID {processId}.",
                exception);
        }

        var commandEnd = stat.LastIndexOf(") ", StringComparison.Ordinal);
        if (commandEnd < 0)
        {
            throw new ProcessTreeProbeException(
                $"Linux returned a malformed process record for PID {processId}.");
        }

        var fields = stat[(commandEnd + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19
            || !int.TryParse(
                fields[1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parentProcessId)
            || !ulong.TryParse(
                fields[19],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var startToken))
        {
            throw new ProcessTreeProbeException(
                $"Linux returned an incomplete process record for PID {processId}.");
        }

        return new LinuxProcess(
            new ProcessIdentity(processId, startToken),
            parentProcessId);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallPidFdOpen(
        long number,
        int processId,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallPidFdSendSignal(
        long number,
        int descriptor,
        int signal,
        IntPtr signalInformation,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LinuxProcess(
        ProcessIdentity Identity,
        int ParentProcessId);

    private readonly record struct LinuxChildReference(int ProcessId);
}

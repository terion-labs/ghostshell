using System.Runtime.InteropServices;

namespace Asura.AccessibilityAcceptance;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct ProcessParent(int ProcessId, int ParentProcessId);

internal static class ProcessTreeProbe
{
    public static IProcessTreeProbe CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacProcessTreeProbe();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsProcessTreeProbe();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxProcessTreeProbe();
        }

        throw new PlatformNotSupportedException(
            "Process-tree tracking supports macOS, Windows, and Linux only.");
    }

    public static IReadOnlyList<(int ProcessId, int Depth)> FindDescendants(
        int rootProcessId,
        IReadOnlyCollection<ProcessParent> processes)
    {
        var children = new Dictionary<int, List<int>>();
        var processIds = new HashSet<int>();
        foreach (var process in processes)
        {
            if (!processIds.Add(process.ProcessId))
            {
                throw new ProcessTreeProbeException(
                    "The operating-system process snapshot contained a duplicate PID.");
            }

            if (!children.TryGetValue(process.ParentProcessId, out var siblings))
            {
                siblings = [];
                children.Add(process.ParentProcessId, siblings);
            }

            siblings.Add(process.ProcessId);
        }

        var descendants = new List<(int ProcessId, int Depth)>();
        var visited = new HashSet<int> { rootProcessId };
        var pending = new Queue<(int ProcessId, int Depth)>();
        pending.Enqueue((rootProcessId, 0));
        while (pending.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent.ProcessId, out var directChildren))
            {
                continue;
            }

            foreach (var child in directChildren)
            {
                if (!visited.Add(child))
                {
                    throw new ProcessTreeProbeException(
                        "The operating-system process snapshot contained a parent cycle.");
                }

                var depth = parent.Depth + 1;
                descendants.Add((child, depth));
                pending.Enqueue((child, depth));
            }
        }

        return descendants;
    }
}

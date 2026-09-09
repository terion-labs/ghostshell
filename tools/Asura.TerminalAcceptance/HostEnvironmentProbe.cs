namespace Asura.TerminalAcceptance;

internal sealed record HostEnvironmentSignals(
    bool AutomationDetected,
    bool ContainerDetected,
    bool UnsupportedDisplayServerDetected,
    bool WaylandDisplayDetected)
{
    public bool BlocksNamedHostAcceptance =>
        AutomationDetected
        || ContainerDetected
        || UnsupportedDisplayServerDetected
        || WaylandDisplayDetected;
}

internal static class HostEnvironmentProbe
{
    private static readonly HashSet<string> UnsupportedXServerProcesses = new(
        ["xvfb", "xephyr", "xvnc", "xtigervnc", "xwayland", "xpra"],
        StringComparer.OrdinalIgnoreCase);

    public static HostEnvironmentSignals Capture(TargetPlatform platform)
    {
        var isLinux = platform == TargetPlatform.LinuxX11;
        return new HostEnvironmentSignals(
            IsAutomationEnvironment(),
            IsContainerEnvironment(),
            isLinux && IsUnsupportedDisplayServer(Environment.GetEnvironmentVariable("DISPLAY")),
            isLinux && IsWaylandEnvironment());
    }

    internal static bool ProcessOwnsDisplay(
        string? display,
        string processName,
        IReadOnlyList<string> arguments)
    {
        var displayArgument = GetLocalDisplayArgument(display);
        return displayArgument is not null
            && UnsupportedXServerProcesses.Contains(processName)
            && arguments.Any(argument => string.Equals(
                argument,
                displayArgument,
                StringComparison.Ordinal));
    }

    internal static bool IsLocalDisplay(string? display) =>
        GetLocalDisplayArgument(display) is not null;

    private static bool IsAutomationEnvironment() =>
        HasEnvironmentMarker("CI")
        || HasEnvironmentMarker("GITHUB_ACTIONS")
        || HasEnvironmentMarker("TF_BUILD")
        || HasEnvironmentMarker("GITLAB_CI")
        || HasEnvironmentMarker("JENKINS_URL")
        || HasEnvironmentMarker("TEAMCITY_VERSION");

    private static bool IsContainerEnvironment()
    {
        if (HasEnvironmentMarker("DOTNET_RUNNING_IN_CONTAINER")
            || HasEnvironmentMarker("container")
            || File.Exists("/.dockerenv")
            || File.Exists("/run/.containerenv"))
        {
            return true;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            var cgroup = File.ReadAllText("/proc/1/cgroup");
            return cgroup.Contains("docker", StringComparison.OrdinalIgnoreCase)
                || cgroup.Contains("containerd", StringComparison.OrdinalIgnoreCase)
                || cgroup.Contains("kubepods", StringComparison.OrdinalIgnoreCase)
                || cgroup.Contains("lxc", StringComparison.OrdinalIgnoreCase)
                || cgroup.Contains("podman", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsUnsupportedDisplayServer(string? display)
    {
        var displayArgument = GetLocalDisplayArgument(display);
        if (string.IsNullOrWhiteSpace(display))
        {
            return false;
        }

        // Remote TCP/SSH displays cannot prove behavior on the named host's own X server.
        if (displayArgument is null)
        {
            return true;
        }

        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return false;
        }

        try
        {
            foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(processDirectory), System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                var processName = TryReadText(Path.Combine(processDirectory, "comm"))?.Trim();
                if (string.IsNullOrWhiteSpace(processName)
                    || !UnsupportedXServerProcesses.Contains(processName))
                {
                    continue;
                }

                var commandLine = TryReadBytes(Path.Combine(processDirectory, "cmdline"));
                if (commandLine is null)
                {
                    continue;
                }

                var arguments = System.Text.Encoding.UTF8
                    .GetString(commandLine)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (ProcessOwnsDisplay(display, processName, arguments))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static bool IsWaylandEnvironment() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
        || string.Equals(
            Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
            "wayland",
            StringComparison.OrdinalIgnoreCase);

    private static bool HasEnvironmentMarker(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetLocalDisplayArgument(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        var value = display.Trim();
        if (value.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["unix".Length..];
        }

        if (!value.StartsWith(':'))
        {
            return null;
        }

        var screenSeparator = value.IndexOf('.', StringComparison.Ordinal);
        return screenSeparator < 0 ? value : value[..screenSeparator];
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? TryReadBytes(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

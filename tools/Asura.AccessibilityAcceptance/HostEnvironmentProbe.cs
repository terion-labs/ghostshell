namespace Asura.AccessibilityAcceptance;

internal static class HostEnvironmentProbe
{
    private static readonly HashSet<string> UnsupportedXServerProcesses = new(
        ["xvfb", "xephyr", "xvnc", "xtigervnc", "xwayland", "xpra", "xnest", "nxagent"],
        StringComparer.OrdinalIgnoreCase);

    public static HostEnvironmentSignals Capture(TargetPlatform platform)
    {
        var isLinux = platform == TargetPlatform.LinuxX11;
        return new HostEnvironmentSignals(
            IsAutomationEnvironment(),
            IsContainerEnvironment(),
            IsRemoteSession(platform),
            isLinux && IsUnsupportedDisplayServer(Environment.GetEnvironmentVariable("DISPLAY")),
            isLinux && IsWaylandEnvironment(),
            !Console.IsInputRedirected,
            !Console.IsOutputRedirected);
    }

    public static IReadOnlyList<string> DescribeBlockers(HostEnvironmentSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var warnings = new List<string>();
        if (signals.AutomationDetected)
        {
            warnings.Add("An automation-environment marker is present.");
        }

        if (signals.ContainerDetected)
        {
            warnings.Add("A container marker is present.");
        }

        if (signals.RemoteSessionDetected)
        {
            warnings.Add("A remote-session marker is present.");
        }

        if (signals.UnsupportedDisplayServerDetected)
        {
            warnings.Add("The active display belongs to a virtual, forwarded, or unsupported X server.");
        }

        if (signals.WaylandDisplayDetected)
        {
            warnings.Add("Wayland or XWayland is present; this catalog requires the supported X11 path.");
        }

        if (!signals.StandardInputInteractive || !signals.StandardOutputInteractive)
        {
            warnings.Add("The runner requires an interactive terminal for operator observations.");
        }

        return warnings;
    }

    public static string DescribeDesktopSession(TargetPlatform platform) => platform switch
    {
        TargetPlatform.MacOS => "macOS local console",
        TargetPlatform.Windows => "Windows local interactive desktop",
        TargetPlatform.LinuxX11 =>
            $"Linux {EvidenceSanitizer.SanitizeIdentifier(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown")} "
            + $"(DISPLAY {(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")) ? "present" : "absent")})",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
    };

    internal static bool IsLocalDisplay(string? display) =>
        GetLocalDisplayArgument(display) is not null;

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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable Linux host boundary cannot establish that this is not a container.
            return true;
        }
    }

    private static bool IsRemoteSession(TargetPlatform platform)
    {
        var sessionName = Environment.GetEnvironmentVariable("SESSIONNAME") ?? string.Empty;
        if (sessionName.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSH_CONNECTION"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSH_TTY")))
        {
            return true;
        }

        return platform == TargetPlatform.LinuxX11
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"))
            && !IsLocalDisplay(Environment.GetEnvironmentVariable("DISPLAY"));
    }

    private static bool IsUnsupportedDisplayServer(string? display)
    {
        var displayArgument = GetLocalDisplayArgument(display);
        if (string.IsNullOrWhiteSpace(display) || displayArgument is null)
        {
            return true;
        }

        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return true;
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

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

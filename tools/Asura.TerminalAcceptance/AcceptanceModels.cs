using System.Runtime.InteropServices;

namespace Asura.TerminalAcceptance;

internal enum TargetPlatform
{
    Windows,
    LinuxX11,
}

internal enum AcceptanceStatus
{
    Pass,
    Fail,
    Blocked,
}

internal sealed record AcceptanceCheck(
    string Id,
    string Title,
    string CommonInstructions,
    string WindowsInstructions,
    string LinuxInstructions)
{
    public string InstructionsFor(TargetPlatform platform) =>
        $"{CommonInstructions} " + (platform switch
        {
            TargetPlatform.Windows => WindowsInstructions,
            TargetPlatform.LinuxX11 => LinuxInstructions,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        });
}

internal sealed record CheckObservation(
    string Id,
    string Title,
    AcceptanceStatus Result,
    string ObservationMode,
    string Notes,
    int RedactionsApplied,
    DateTimeOffset ObservedAtUtc);

internal sealed record HostIdentity(
    string DeclaredSystemName,
    string ActualHostName,
    string Observer,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    string DesktopSession,
    bool InteractiveUser,
    bool RemoteSessionDetected,
    HostEnvironmentSignals EnvironmentSignals,
    IReadOnlyList<string> EnvironmentWarnings)
{
    public static HostIdentity Capture(
        TargetPlatform platform,
        string declaredSystemName,
        string observer)
    {
        var sessionName = Environment.GetEnvironmentVariable("SESSIONNAME") ?? string.Empty;
        var remoteSessionDetected = sessionName.StartsWith("RDP-", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSH_CONNECTION"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSH_TTY"));
        var environment = HostEnvironmentProbe.Capture(platform);
        var warnings = new List<string>();
        var desktopSession = platform switch
        {
            TargetPlatform.Windows => "Windows interactive desktop",
            TargetPlatform.LinuxX11 => DescribeLinuxDesktopSession(warnings),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
        };

        if (remoteSessionDetected)
        {
            warnings.Add(
                "A remote-session marker is present; direct keyboard, pointer, IME, compositor, and sleep observations may be blocked.");
        }

        if (environment.AutomationDetected)
        {
            warnings.Add(
                "An automation-environment marker is present; named-host physical acceptance is blocked.");
        }

        if (environment.ContainerDetected)
        {
            warnings.Add(
                "A container-environment marker is present; named-host physical acceptance is blocked.");
        }

        if (environment.UnsupportedDisplayServerDetected)
        {
            warnings.Add(
                "The active DISPLAY belongs to a virtual or unsupported X server; named-host X11 acceptance is blocked.");
        }

        if (environment.WaylandDisplayDetected)
        {
            warnings.Add(
                "Wayland or XWayland is present; named-host X11 acceptance is blocked.");
        }

        return new HostIdentity(
            declaredSystemName,
            EvidenceSanitizer.SanitizeIdentifier(Environment.MachineName),
            observer,
            EvidenceSanitizer.SanitizeSingleLine(RuntimeInformation.OSDescription).Value,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            desktopSession,
            Environment.UserInteractive,
            remoteSessionDetected,
            environment,
            warnings);
    }

    private static string DescribeLinuxDesktopSession(List<string> warnings)
    {
        var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        var hasDisplay = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
        var hasWaylandDisplay = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        if (!string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase) || !hasDisplay)
        {
            warnings.Add(
                "The process is not in a confirmed X11 session with DISPLAY; Linux X11 acceptance must be recorded as BLOCKED.");
        }

        if (hasWaylandDisplay || string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(
                "Wayland or XWayland is present; it cannot prove Asura's currently supported X11-global shortcut behavior.");
        }

        return $"Linux {EvidenceSanitizer.SanitizeIdentifier(sessionType ?? "unknown")} "
            + $"(DISPLAY {(hasDisplay ? "present" : "absent")}, "
            + $"Wayland display {(hasWaylandDisplay ? "present" : "absent")})";
    }
}

internal sealed record BackendIdentity(
    string Renderer,
    string PtyAdapter,
    string PtySubstrate,
    string IdentitySource);

internal sealed record BuildIdentity(
    string BuildLabel,
    string PackageExecutable,
    string ProductVersion,
    long ExecutableLengthBytes,
    string ExecutableSha256,
    int PackageFileCount,
    string PackageManifestSha256);

internal sealed record AcceptanceEvidence(
    int SchemaVersion,
    string EvidenceKind,
    string RunnerVersion,
    TargetPlatform Platform,
    HostIdentity Host,
    BackendIdentity Backend,
    BuildIdentity Build,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    AcceptanceStatus OverallResult,
    string CleanupDisposition,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<CheckObservation> Checks)
{
    public const int CurrentSchemaVersion = 3;
    public const string CurrentEvidenceKind = "asura-named-host-m2-terminal-acceptance";
    public const string CurrentRunnerVersion = "1.1.0";
    public const string CleanExitDisposition =
        "Package exited before runner cleanup; no process termination was required.";

    public static IReadOnlyList<string> StandardLimitations { get; } =
    [
        "PASS means a named operator observed the packaged build on this one host; the runner never infers a physical result from unit tests or a virtual display.",
        "The runner captures no screenshots, raw terminal logs, shell history, clipboard payloads, environment dump, remote address, or credential value.",
        "This evidence does not apply to another OS, desktop backend, package digest, terminal renderer, or PTY adapter.",
        "The SHA-256 sidecar detects file changes but is not a signature and does not authenticate the operator or host.",
    ];

    public static AcceptanceStatus ResolveOverall(IReadOnlyCollection<CheckObservation> checks)
    {
        if (checks.Any(check => check.Result == AcceptanceStatus.Fail))
        {
            return AcceptanceStatus.Fail;
        }

        return checks.Count == AcceptanceCatalog.All.Count
            && checks.All(check => check.Result == AcceptanceStatus.Pass)
                ? AcceptanceStatus.Pass
                : AcceptanceStatus.Blocked;
    }
}

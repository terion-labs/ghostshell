using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Asura.AccessibilityAcceptance;

internal sealed record ScreenReaderSnapshot(
    bool Verified,
    ScreenReaderKind Kind,
    string Product,
    string Version,
    string IdentitySource,
    string StatusCode,
    string AccessibilityBusStatus);

internal static partial class ScreenReaderProbe
{
    internal const string OrcaIdentitySource =
        "running system Orca process bound to its interpreter plus queried AT-SPI desktop bus";
    private const int VersionProbeTimeoutMilliseconds = 5_000;
    private const string VoiceOverExecutable =
        "/System/Library/CoreServices/VoiceOver.app/Contents/MacOS/VoiceOver";
    private const string OrcaLauncher = "/usr/bin/orca";

    public static ScreenReaderSnapshot Capture(
        TargetPlatform platform,
        ScreenReaderKind expected)
    {
        try
        {
            return platform switch
            {
                TargetPlatform.MacOS when expected == ScreenReaderKind.VoiceOver =>
                    CaptureVoiceOver(),
                TargetPlatform.Windows when expected == ScreenReaderKind.Narrator =>
                    CaptureNarrator(),
                TargetPlatform.LinuxX11 when expected == ScreenReaderKind.Orca =>
                    CaptureOrca(),
                _ => new ScreenReaderSnapshot(
                    false,
                    expected,
                    expected.ToString(),
                    "unavailable",
                    "unsupported-platform-reader-pair",
                    "UNSUPPORTED_MAPPING",
                    "UNAVAILABLE"),
            };
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or System.ComponentModel.Win32Exception)
        {
            return Unavailable(
                expected,
                "PROBE_FAILED",
                platform == TargetPlatform.LinuxX11
                    ? "AT_SPI_SESSION_BUS_UNAVAILABLE"
                    : "NATIVE_PLATFORM_ACCESSIBILITY");
        }
    }

    internal static bool IsExpectedVoiceOverPath(string path) =>
        string.Equals(path, VoiceOverExecutable, StringComparison.Ordinal);

    internal static bool IsExpectedNarratorPath(string path, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(windowsDirectory))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            var expected = Path.GetFullPath(Path.Combine(
                windowsDirectory,
                "System32",
                "Narrator.exe"));
            return string.Equals(
                Path.GetFullPath(path),
                expected,
                StringComparison.OrdinalIgnoreCase);
        }

        var normalizedPath = path.Replace('/', '\\').TrimEnd('\\');
        var normalizedExpected = $"{windowsDirectory.TrimEnd('\\', '/')}\\System32\\Narrator.exe"
            .Replace('/', '\\');
        return string.Equals(
            normalizedPath,
            normalizedExpected,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ResolveExpectedOrcaLauncher(
        string? liveExecutable,
        IReadOnlyList<string> arguments)
    {
        if (string.Equals(liveExecutable, OrcaLauncher, StringComparison.Ordinal))
        {
            return OrcaLauncher;
        }

        if (!IsSystemPython(liveExecutable))
        {
            return null;
        }

        var normalInterpreterLaunch = arguments.Count >= 2
            && IsSystemPython(arguments[0])
            && string.Equals(arguments[1], OrcaLauncher, StringComparison.Ordinal);
        var rewrittenOrcaTitle = arguments.Count >= 1
            && string.Equals(arguments[0], "orca", StringComparison.Ordinal);
        return normalInterpreterLaunch || rewrittenOrcaTitle ? OrcaLauncher : null;
    }

    internal static bool IsAtSpiAddressResponse(string? response) =>
        !string.IsNullOrWhiteSpace(response)
        && response.Contains("unix:", StringComparison.Ordinal)
        && response.Contains("/at-spi/", StringComparison.Ordinal);

    private static ScreenReaderSnapshot CaptureVoiceOver()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return Unavailable(ScreenReaderKind.VoiceOver, "PLATFORM_MISMATCH", "NATIVE_PLATFORM_ACCESSIBILITY");
        }

        using var process = ExactlyOneProcess("VoiceOver");
        if (process is null)
        {
            return Unavailable(ScreenReaderKind.VoiceOver, "NOT_EXACTLY_ONE_RUNNING", "NATIVE_PLATFORM_ACCESSIBILITY");
        }

        var executable = TryGetMainModulePath(process);
        if (executable is null || !IsExpectedVoiceOverPath(executable))
        {
            return Unavailable(ScreenReaderKind.VoiceOver, "IDENTITY_UNVERIFIED", "NATIVE_PLATFORM_ACCESSIBILITY");
        }

        var bundleRoot = executable[..executable.LastIndexOf("/Contents/MacOS/VoiceOver", StringComparison.Ordinal)];
        var infoPlist = Path.Combine(bundleRoot, "Contents", "Info.plist");
        var identifier = RunBounded("/usr/bin/plutil", ["-extract", "CFBundleIdentifier", "raw", "-o", "-", infoPlist]);
        var version = RunBounded("/usr/bin/plutil", ["-extract", "CFBundleShortVersionString", "raw", "-o", "-", infoPlist]);
        if (!string.Equals(identifier, "com.apple.VoiceOver", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(version)
            || !EvidenceSanitizer.IsSafeVersionText(version))
        {
            return Unavailable(ScreenReaderKind.VoiceOver, "IDENTITY_UNVERIFIED", "NATIVE_PLATFORM_ACCESSIBILITY");
        }

        return Verified(
            ScreenReaderKind.VoiceOver,
            "Apple VoiceOver",
            version,
            "running system application with bundle identifier com.apple.VoiceOver",
            "NATIVE_PLATFORM_ACCESSIBILITY");
    }

    private static ScreenReaderSnapshot CaptureNarrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Unavailable(ScreenReaderKind.Narrator, "PLATFORM_MISMATCH", "NATIVE_PLATFORM_ACCESSIBILITY");
        }

        using var process = ExactlyOneProcess("Narrator");
        if (process is null)
        {
            return Unavailable(ScreenReaderKind.Narrator, "NOT_EXACTLY_ONE_RUNNING", "NATIVE_PLATFORM_ACCESSIBILITY");
        }

        var executable = TryGetMainModulePath(process);
        // SpecialFolder.Windows is resolved by the runtime from the host API.
        // WINDIR is mutable process input and cannot anchor a system executable.
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (executable is null || !IsExpectedNarratorPath(executable, windowsDirectory))
        {
            return Unavailable(ScreenReaderKind.Narrator, "IDENTITY_UNVERIFIED", "NATIVE_PLATFORM_ACCESSIBILITY");
        }

        var versionInfo = FileVersionInfo.GetVersionInfo(executable);
        var version = versionInfo.ProductVersion ?? versionInfo.FileVersion;
        if (string.IsNullOrWhiteSpace(version)
            || !EvidenceSanitizer.IsSafeVersionText(version))
        {
            return Unavailable(ScreenReaderKind.Narrator, "VERSION_UNAVAILABLE", "NATIVE_PLATFORM_ACCESSIBILITY");
        }

        return Verified(
            ScreenReaderKind.Narrator,
            "Microsoft Narrator",
            version,
            "running executable verified as Windows System32 Narrator.exe",
            "NATIVE_PLATFORM_ACCESSIBILITY");
    }

    private static ScreenReaderSnapshot CaptureOrca()
    {
        const string busUnavailable = "AT_SPI_SESSION_BUS_UNAVAILABLE";
        if (!OperatingSystem.IsLinux())
        {
            return Unavailable(ScreenReaderKind.Orca, "PLATFORM_MISMATCH", busUnavailable);
        }

        var sessionBusAvailable = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        var atSpiAddress = sessionBusAvailable
            ? RunBounded(
                "/usr/bin/gdbus",
                [
                    "call",
                    "--session",
                    "--dest", "org.a11y.Bus",
                    "--object-path", "/org/a11y/bus",
                    "--method", "org.a11y.Bus.GetAddress",
                ])
            : null;
        var busStatus = IsAtSpiAddressResponse(atSpiAddress)
            ? "AT_SPI_SESSION_BUS_PRESENT"
            : busUnavailable;
        using var process = ExactlyOneProcess("orca");
        if (process is null)
        {
            return Unavailable(ScreenReaderKind.Orca, "NOT_EXACTLY_ONE_RUNNING", busStatus);
        }

        var arguments = TryReadLinuxArguments(process.Id);
        var liveExecutable = TryReadLinuxExecutable(process.Id);
        var launcher = arguments is null
            ? null
            : ResolveExpectedOrcaLauncher(liveExecutable, arguments);
        if (launcher is null || !File.Exists(launcher))
        {
            return Unavailable(ScreenReaderKind.Orca, "IDENTITY_UNVERIFIED", busStatus);
        }

        var versionOutput = RunBounded(launcher, ["--version"]);
        if (!TryParseOrcaVersion(versionOutput, out var version))
        {
            return Unavailable(ScreenReaderKind.Orca, "VERSION_UNAVAILABLE", busStatus);
        }

        if (!string.Equals(busStatus, "AT_SPI_SESSION_BUS_PRESENT", StringComparison.Ordinal))
        {
            return Unavailable(ScreenReaderKind.Orca, "ACCESSIBILITY_BUS_UNAVAILABLE", busStatus);
        }

        return Verified(
            ScreenReaderKind.Orca,
            "GNOME Orca",
            version,
            OrcaIdentitySource,
            busStatus);
    }

    internal static bool TryParseOrcaVersion(string? output, out string version)
    {
        var match = OrcaVersion().Match(output ?? string.Empty);
        version = match.Success ? match.Groups["version"].Value : string.Empty;
        return match.Success && EvidenceSanitizer.IsSafeVersionText(version);
    }

    private static Process? ExactlyOneProcess(string name)
    {
        var processes = Process.GetProcessesByName(name);
        if (processes.Length == 1)
        {
            return processes[0];
        }

        foreach (var process in processes)
        {
            process.Dispose();
        }

        return null;
    }

    private static string? TryGetMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? TryReadLinuxArguments(int processId)
    {
        try
        {
            return Encoding.UTF8
                .GetString(File.ReadAllBytes($"/proc/{processId}/cmdline"))
                .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryReadLinuxExecutable(int processId)
    {
        try
        {
            return new FileInfo($"/proc/{processId}/exe")
                .ResolveLinkTarget(returnFinalTarget: true)
                ?.FullName;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsSystemPython(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !string.Equals(Path.GetDirectoryName(path), "/usr/bin", StringComparison.Ordinal))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        if (string.Equals(name, "python3", StringComparison.Ordinal))
        {
            return true;
        }

        const string versionedPrefix = "python3.";
        var version = name.StartsWith(versionedPrefix, StringComparison.Ordinal)
            ? name[versionedPrefix.Length..]
            : string.Empty;
        return version.Length > 0
            && version.All(character =>
                char.IsAsciiDigit(character) || character == '.');
    }

    private static string? RunBounded(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null || !process.WaitForExit(VersionProbeTimeoutMilliseconds))
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            return EvidenceSanitizer.SanitizeSingleLine(process.StandardOutput.ReadToEnd()).Value;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static ScreenReaderSnapshot Verified(
        ScreenReaderKind kind,
        string product,
        string version,
        string identitySource,
        string busStatus) =>
        new(true, kind, product, version, identitySource, "ACTIVE_VERIFIED", busStatus);

    private static ScreenReaderSnapshot Unavailable(
        ScreenReaderKind kind,
        string status,
        string busStatus) =>
        new(false, kind, kind.ToString(), "unavailable", "identity unavailable", status, busStatus);

    [GeneratedRegex(
        @"^Orca version\s+(?<version>[A-Za-z0-9][A-Za-z0-9._+()~-]*)",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex OrcaVersion();
}

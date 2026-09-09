using Asura.Application;
using Asura.Core;

namespace Asura.Terminal;

/// <summary>
/// Ports Ghostty's process-side shell-integration setup without depending on
/// Ghostty's application runtime. The resulting request is used only to spawn
/// the PTY; terminal state continues to belong to libghostty-vt.
/// </summary>
internal sealed class GhosttyShellIntegrationLaunchAdapter
{
    private const string ShellIntegrationDirectoryName = "shell-integration";
    private const string ShellFeaturesEnvironmentName = "GHOSTTY_SHELL_FEATURES";
    private readonly string _resourceDirectory;

    public GhosttyShellIntegrationLaunchAdapter()
        : this(ResolveResourceDirectory(AppContext.BaseDirectory))
    {
    }

    internal GhosttyShellIntegrationLaunchAdapter(string resourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceDirectory);
        _resourceDirectory = Path.GetFullPath(resourceDirectory);
    }

    internal static string ResolveResourceDirectory(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        var adjacentResources = Path.GetFullPath(Path.Combine(
            baseDirectory,
            "..",
            "Resources",
            "ghostty"));
        return Directory.Exists(adjacentResources)
            ? adjacentResources
            : Path.GetFullPath(Path.Combine(baseDirectory, "ghostty"));
    }

    public GhosttyShellIntegrationPreparation Prepare(TerminalLaunchRequest launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        var requestedMode = launch.RenderProfile?.ShellIntegration
            ?? TerminalShellIntegrationMode.Detect;
        if (requestedMode == TerminalShellIntegrationMode.Disabled)
        {
            return Unchanged(
                launch,
                GhosttyShellIntegrationPreparationStatus.Disabled,
                null,
                "Shell integration is disabled by the terminal profile.");
        }

        var executable = launch.Executable ?? ResolveDefaultShell();
        var detectedMode = DetectShell(executable);
        var selectedMode = requestedMode == TerminalShellIntegrationMode.Detect
            ? detectedMode
            : requestedMode;
        if (selectedMode is null)
        {
            return Unchanged(
                launch,
                GhosttyShellIntegrationPreparationStatus.NotDetected,
                null,
                $"No supported interactive shell was detected for '{executable}'.");
        }

        if (selectedMode is TerminalShellIntegrationMode.Elvish
            or TerminalShellIntegrationMode.Nushell)
        {
            var detail = $"Automatic {selectedMode} shell integration is not yet supported "
                + "by the managed terminal launch path.";
            if (requestedMode != TerminalShellIntegrationMode.Detect)
            {
                throw new PlatformNotSupportedException(detail);
            }

            return Unchanged(
                launch,
                GhosttyShellIntegrationPreparationStatus.UnsupportedShell,
                selectedMode,
                detail);
        }

        if (requestedMode != TerminalShellIntegrationMode.Detect
            && detectedMode != selectedMode)
        {
            return Unchanged(
                launch,
                GhosttyShellIntegrationPreparationStatus.IncompatibleLaunch,
                selectedMode,
                $"The configured {selectedMode} integration cannot be injected into "
                    + $"the '{Path.GetFileName(executable)}' process.");
        }

        if (OperatingSystem.IsWindows())
        {
            var detail = $"Automatic {selectedMode} shell integration requires a POSIX host.";
            if (requestedMode != TerminalShellIntegrationMode.Detect)
            {
                throw new PlatformNotSupportedException(detail);
            }

            return Unchanged(
                launch,
                GhosttyShellIntegrationPreparationStatus.UnsupportedShell,
                selectedMode,
                detail);
        }

        return selectedMode switch
        {
            TerminalShellIntegrationMode.Bash => PrepareBash(launch, executable),
            TerminalShellIntegrationMode.Fish => PrepareFish(launch, executable),
            TerminalShellIntegrationMode.Zsh => PrepareZsh(launch, executable),
            _ => throw new ArgumentOutOfRangeException(
                nameof(launch),
                selectedMode,
                "Unknown shell-integration mode."),
        };
    }

    private GhosttyShellIntegrationPreparation PrepareBash(
        TerminalLaunchRequest launch,
        string executable)
    {
        // Apple's SIP-protected Bash 3.2 disables the ENV startup path that
        // Ghostty's automatic bootstrap depends on.
        if (OperatingSystem.IsMacOS()
            && string.Equals(executable, "/bin/bash", StringComparison.Ordinal))
        {
            return Unchanged(
                launch,
                GhosttyShellIntegrationPreparationStatus.IncompatibleLaunch,
                TerminalShellIntegrationMode.Bash,
                "Apple's /bin/bash does not support automatic ENV-based integration.");
        }

        var script = Path.Combine(
            _resourceDirectory,
            ShellIntegrationDirectoryName,
            "bash",
            "ghostty.bash");
        var preexecScript = Path.Combine(
            _resourceDirectory,
            ShellIntegrationDirectoryName,
            "bash",
            "bash-preexec.sh");
        if (!File.Exists(script) || !File.Exists(preexecScript))
        {
            return ResourcesUnavailable(launch, TerminalShellIntegrationMode.Bash);
        }

        var rewrittenArguments = RewriteBashArguments(launch.Arguments);
        if (rewrittenArguments is null)
        {
            return Unchanged(
                launch,
                GhosttyShellIntegrationPreparationStatus.IncompatibleLaunch,
                TerminalShellIntegrationMode.Bash,
                "Bash command and POSIX-mode launches cannot be safely injected.");
        }

        var environment = SnapshotEnvironment(launch);
        SetCommonEnvironment(environment, launch);
        PreserveEnvironment(environment, "ENV", "GHOSTTY_BASH_ENV");
        environment["ENV"] = script;
        environment["GHOSTTY_BASH_INJECT"] = rewrittenArguments.InjectionFlags;
        if (rewrittenArguments.RcFile is not null)
        {
            environment["GHOSTTY_BASH_RCFILE"] = rewrittenArguments.RcFile;
        }

        if (!ContainsEnvironment(environment, "HISTFILE"))
        {
            var home = ReadEnvironment(environment, "HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                environment["HISTFILE"] = Path.Combine(home, ".bash_history");
                environment["GHOSTTY_BASH_UNEXPORT_HISTFILE"] = "1";
            }
        }

        return Applied(
            launch,
            executable,
            rewrittenArguments.Arguments,
            environment,
            TerminalShellIntegrationMode.Bash);
    }

    private GhosttyShellIntegrationPreparation PrepareFish(
        TerminalLaunchRequest launch,
        string executable)
    {
        var integrationDirectory = Path.Combine(
            _resourceDirectory,
            ShellIntegrationDirectoryName);
        var script = Path.Combine(
            integrationDirectory,
            "fish",
            "vendor_conf.d",
            "ghostty-shell-integration.fish");
        if (!File.Exists(script))
        {
            return ResourcesUnavailable(launch, TerminalShellIntegrationMode.Fish);
        }

        var environment = SnapshotEnvironment(launch);
        SetCommonEnvironment(environment, launch);
        environment["GHOSTTY_SHELL_INTEGRATION_XDG_DIR"] = integrationDirectory;
        var current = ReadEnvironment(environment, "XDG_DATA_DIRS")
            ?? "/usr/local/share:/usr/share";
        environment["XDG_DATA_DIRS"] = string.IsNullOrEmpty(current)
            ? integrationDirectory
            : $"{integrationDirectory}{Path.PathSeparator}{current}";
        return Applied(
            launch,
            executable,
            launch.Arguments,
            environment,
            TerminalShellIntegrationMode.Fish);
    }

    private GhosttyShellIntegrationPreparation PrepareZsh(
        TerminalLaunchRequest launch,
        string executable)
    {
        var integrationDirectory = Path.Combine(
            _resourceDirectory,
            ShellIntegrationDirectoryName,
            "zsh");
        if (!File.Exists(Path.Combine(integrationDirectory, ".zshenv"))
            || !File.Exists(Path.Combine(integrationDirectory, "ghostty-integration")))
        {
            return ResourcesUnavailable(launch, TerminalShellIntegrationMode.Zsh);
        }

        var environment = SnapshotEnvironment(launch);
        SetCommonEnvironment(environment, launch);
        PreserveEnvironment(environment, "ZDOTDIR", "GHOSTTY_ZSH_ZDOTDIR");
        environment["ZDOTDIR"] = integrationDirectory;
        return Applied(
            launch,
            executable,
            launch.Arguments,
            environment,
            TerminalShellIntegrationMode.Zsh);
    }

    private void SetCommonEnvironment(
        Dictionary<string, string> environment,
        TerminalLaunchRequest launch)
    {
        var cursor = launch.RenderProfile?.CursorBlink == false ? "steady" : "blink";
        environment["GHOSTTY_RESOURCES_DIR"] = _resourceDirectory;
        environment[ShellFeaturesEnvironmentName] = $"cursor:{cursor},path,title";
    }

    private static BashArguments? RewriteBashArguments(IReadOnlyList<string> arguments)
    {
        var rewritten = new List<string>(arguments.Count + 1) { "--posix" };
        var injection = new List<string> { "1" };
        string? rcFile = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (string.Equals(argument, "--posix", StringComparison.Ordinal))
            {
                return null;
            }

            if (string.Equals(argument, "--norc", StringComparison.Ordinal)
                || string.Equals(argument, "--noprofile", StringComparison.Ordinal))
            {
                injection.Add(argument);
                continue;
            }

            if (string.Equals(argument, "--rcfile", StringComparison.Ordinal)
                || string.Equals(argument, "--init-file", StringComparison.Ordinal))
            {
                if (++index >= arguments.Count)
                {
                    return null;
                }

                rcFile = arguments[index];
                continue;
            }

            if (argument.Length > 1
                && argument[0] == '-'
                && argument[1] != '-'
                && argument.Contains('c'))
            {
                return null;
            }

            rewritten.Add(argument);
            if (argument is "-" or "--")
            {
                for (index++; index < arguments.Count; index++)
                {
                    rewritten.Add(arguments[index]);
                }

                break;
            }
        }

        return new BashArguments(
            Array.AsReadOnly(rewritten.ToArray()),
            string.Join(' ', injection),
            rcFile);
    }

    private static Dictionary<string, string> SnapshotEnvironment(
        TerminalLaunchRequest launch) =>
        new(launch.Environment, StringComparer.Ordinal);

    private static void PreserveEnvironment(
        Dictionary<string, string> environment,
        string sourceName,
        string preservedName)
    {
        var existing = ReadEnvironment(environment, sourceName);
        if (existing is not null)
        {
            environment[preservedName] = existing;
        }
    }

    private static bool ContainsEnvironment(
        IReadOnlyDictionary<string, string> environment,
        string name) =>
        environment.ContainsKey(name)
        || Environment.GetEnvironmentVariable(name) is not null;

    private static string? ReadEnvironment(
        IReadOnlyDictionary<string, string> environment,
        string name) =>
        environment.TryGetValue(name, out var value)
            ? value
            : Environment.GetEnvironmentVariable(name);

    private static TerminalShellIntegrationMode? DetectShell(string executable) =>
        Path.GetFileName(executable) switch
        {
            "bash" => TerminalShellIntegrationMode.Bash,
            "elvish" => TerminalShellIntegrationMode.Elvish,
            "fish" => TerminalShellIntegrationMode.Fish,
            "nu" => TerminalShellIntegrationMode.Nushell,
            "zsh" => TerminalShellIntegrationMode.Zsh,
            _ => null,
        };

    private static string ResolveDefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("COMSPEC")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

        var configured = Environment.GetEnvironmentVariable("SHELL");
        return !string.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured)
            ? configured
            : "/bin/sh";
    }

    private static GhosttyShellIntegrationPreparation Applied(
        TerminalLaunchRequest original,
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TerminalShellIntegrationMode shell) =>
        new(
            CopyLaunch(original, executable, arguments, environment),
            GhosttyShellIntegrationPreparationStatus.Applied,
            shell,
            null);

    private GhosttyShellIntegrationPreparation ResourcesUnavailable(
        TerminalLaunchRequest launch,
        TerminalShellIntegrationMode shell) =>
        Unchanged(
            launch,
            GhosttyShellIntegrationPreparationStatus.ResourcesUnavailable,
            shell,
            $"The pinned {shell} integration resources are unavailable under "
                + $"'{_resourceDirectory}'.");

    private static GhosttyShellIntegrationPreparation Unchanged(
        TerminalLaunchRequest launch,
        GhosttyShellIntegrationPreparationStatus status,
        TerminalShellIntegrationMode? shell,
        string detail) =>
        new(launch, status, shell, detail);

    private static TerminalLaunchRequest CopyLaunch(
        TerminalLaunchRequest original,
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment) =>
        new(
            original.WorkingDirectory,
            executable,
            arguments,
            environment,
            original.RenderProfile,
            original.Keymap,
            original.ConnectionId,
            original.ConnectionMetadata);

    private sealed record BashArguments(
        IReadOnlyList<string> Arguments,
        string InjectionFlags,
        string? RcFile);
}

using System.Collections.ObjectModel;

namespace Asura.Infrastructure;

/// <summary>
/// A trusted command for starting this managed desktop entry point again. Prefix arguments contain
/// only the immutable managed assembly path consumed by the dotnet host, never user arguments.
/// </summary>
public sealed record SelfReentryLaunch
{
    public SelfReentryLaunch(
        string executable,
        IReadOnlyList<string>? prefixArguments,
        string askpassExecutable,
        IReadOnlyDictionary<string, string>? askpassEnvironment = null)
    {
        Executable = Validate(executable, nameof(executable));
        AskpassExecutable = Validate(askpassExecutable, nameof(askpassExecutable));
        PrefixArguments = Array.AsReadOnly(prefixArguments?.Select(
            argument => Validate(argument, nameof(prefixArguments))).ToArray() ?? []);
        AskpassEnvironment = new ReadOnlyDictionary<string, string>(
            askpassEnvironment?.ToDictionary(
                item => Validate(item.Key, nameof(askpassEnvironment)),
                item => Validate(item.Value, nameof(askpassEnvironment)),
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public string Executable { get; }

    public IReadOnlyList<string> PrefixArguments { get; }

    /// <summary>An apphost OpenSSH can execute directly because SSH_ASKPASS accepts no prefix argv.</summary>
    public string AskpassExecutable { get; }

    /// <summary>Runtime discovery values needed only when an apphost reenters a dotnet-hosted app.</summary>
    public IReadOnlyDictionary<string, string> AskpassEnvironment { get; }

    public static SelfReentryLaunch Detect() =>
        Detect(Path.Combine(AppContext.BaseDirectory, "Asura.dll"));

    public static SelfReentryLaunch Detect(string managedEntryAssemblyPath) =>
        Detect(
            Environment.ProcessPath
                ?? throw new PlatformNotSupportedException(
                    "The current executable path is unavailable."),
            managedEntryAssemblyPath,
            File.Exists);

    internal static SelfReentryLaunch Detect(
        string processPath,
        string managedEntryAssemblyPath,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        processPath = Validate(processPath, nameof(processPath));
        if (!IsDotnetHost(processPath))
        {
            return new SelfReentryLaunch(processPath, [], processPath);
        }

        managedEntryAssemblyPath = Validate(
            managedEntryAssemblyPath,
            nameof(managedEntryAssemblyPath));
        if (!Path.IsPathFullyQualified(managedEntryAssemblyPath)
            || !fileExists(managedEntryAssemblyPath))
        {
            throw new PlatformNotSupportedException(
                "The managed desktop entry assembly is unavailable for self-reentry.");
        }

        var appHost = Path.Combine(
            Path.GetDirectoryName(managedEntryAssemblyPath)!,
            $"{Path.GetFileNameWithoutExtension(managedEntryAssemblyPath)}{(OperatingSystem.IsWindows() ? ".exe" : string.Empty)}");
        if (!fileExists(appHost))
        {
            throw new PlatformNotSupportedException(
                "OpenSSH askpass requires the desktop apphost next to the managed entry assembly.");
        }

        var dotnetRoot = Path.GetDirectoryName(processPath)!;
        return new SelfReentryLaunch(
            processPath,
            [managedEntryAssemblyPath],
            appHost,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOTNET_ROOT"] = dotnetRoot,
            });
    }

    public override string ToString() =>
        $"Self reentry ({Path.GetFileName(Executable)}, {PrefixArguments.Count} prefix arguments)";

    private static bool IsDotnetHost(string processPath) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(processPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

    private static string Validate(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A self-reentry launch value is invalid.", parameterName);
        }

        return value;
    }
}

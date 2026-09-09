using System.Runtime.InteropServices;

namespace Asura.Infrastructure;

/// <summary>
/// Resolves product-owned connection engines from the application payload while
/// preserving ordinary PATH discovery for unrelated user tools.
/// </summary>
public sealed class BundledConnectionEngineExecutableLocator : IConnectionExecutableLocator
{
    private static readonly HashSet<string> EngineNames = new(StringComparer.Ordinal)
    {
        "asura-openvpn-engine",
        "openconnect",
        "tailscale",
        "tailscaled",
    };

    private readonly IConnectionExecutableLocator _fallback;
    private readonly string _bundleDirectory;
    private readonly bool _useBundle;

    public BundledConnectionEngineExecutableLocator(IConnectionExecutableLocator fallback)
        : this(
            fallback,
            Path.Combine(
                AppContext.BaseDirectory,
                "runtimes",
                "osx-arm64",
                "connection-engines"),
            OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
    {
    }

    internal BundledConnectionEngineExecutableLocator(
        IConnectionExecutableLocator fallback,
        string bundleDirectory,
        bool useBundle)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        _bundleDirectory = Path.GetFullPath(bundleDirectory);
        _useBundle = useBundle;
    }

    public string? Find(string executable)
    {
        if (!_useBundle || !EngineNames.Contains(executable))
        {
            return _fallback.Find(executable);
        }

        var candidate = Path.Combine(_bundleDirectory, executable);
        if (!File.Exists(candidate))
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.GetFullPath(candidate);
        }

        try
        {
            var mode = File.GetUnixFileMode(candidate);
            const UnixFileMode executableBits =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (mode & executableBits) == UnixFileMode.None
                ? null
                : Path.GetFullPath(candidate);
        }
        catch (PlatformNotSupportedException)
        {
            return Path.GetFullPath(candidate);
        }
    }
}

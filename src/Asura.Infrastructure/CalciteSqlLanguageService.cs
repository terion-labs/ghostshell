using System.Diagnostics;
using System.Runtime.InteropServices;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Locates and starts the optional GraalVM-compiled Calcite worker. A missing
/// payload is a supported installation state, so callers always receive a safe
/// session instead of making desktop composition fail.
/// </summary>
public sealed class CalciteSqlLanguageService : ISqlLanguageService
{
    internal const string WorkerPathEnvironment = "ASURA_SQL_LANGUAGE_WORKER";
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly SqlLanguageWorkerLaunch? _launch;
    private readonly TimeSpan _requestTimeout;

    public CalciteSqlLanguageService()
        : this(ResolveWorkerLaunch(), DefaultRequestTimeout)
    {
    }

    internal CalciteSqlLanguageService(
        SqlLanguageWorkerLaunch? launch,
        TimeSpan requestTimeout)
    {
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        _launch = launch;
        _requestTimeout = requestTimeout;
    }

    public bool IsAvailable => _launch is not null;

    public async Task<ISqlLanguageSession> OpenSessionAsync(
        SqlCatalogSnapshot catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        if (_launch is null)
        {
            return UnavailableSqlLanguageSession.Instance;
        }

        var session = new CalciteSqlLanguageSession(_launch, catalog, _requestTimeout);
        await session.TryInitializeAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }

    internal static SqlLanguageWorkerLaunch? ResolveWorkerLaunch()
    {
        var configured = Environment.GetEnvironmentVariable(WorkerPathEnvironment);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string explicitPath;
            try
            {
                explicitPath = Path.GetFullPath(configured);
            }
            catch (Exception exception) when (exception is
                ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            return File.Exists(explicitPath)
                ? new SqlLanguageWorkerLaunch(explicitPath, [])
                : null;
        }

        var runtimeIdentifier = CurrentRuntimeIdentifier();
        if (runtimeIdentifier is null)
        {
            return null;
        }

        var executable = OperatingSystem.IsWindows()
            ? "asura-sql-language.exe"
            : "asura-sql-language";
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            runtimeIdentifier,
            "native",
            executable);
        return File.Exists(path)
            ? new SqlLanguageWorkerLaunch(path, [])
            : null;
    }

    private static string? CurrentRuntimeIdentifier() =>
        (OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux(),
            RuntimeInformation.ProcessArchitecture) switch
        {
            (true, false, false, Architecture.X64) => "win-x64",
            (true, false, false, Architecture.Arm64) => "win-arm64",
            (false, true, false, Architecture.X64) => "osx-x64",
            (false, true, false, Architecture.Arm64) => "osx-arm64",
            (false, false, true, Architecture.X64) => "linux-x64",
            (false, false, true, Architecture.Arm64) => "linux-arm64",
            _ => null,
        };
}

internal sealed record SqlLanguageWorkerLaunch(
    string Executable,
    IReadOnlyList<string> Arguments)
{
    private static readonly string[] SafeEnvironmentVariables =
    [
        "LANG",
        "LC_ALL",
        "LC_CTYPE",
        "TZ",
        "TMPDIR",
        "TMP",
        "TEMP",
        "SystemRoot",
        "WINDIR",
    ];

    public Process CreateProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Database/cloud/API credentials are commonly supplied to the desktop
        // through environment variables. The language worker needs only SQL
        // text and the detached catalog written over stdin, so do not leak the
        // parent's authority across this process boundary.
        startInfo.Environment.Clear();
        foreach (var name in SafeEnvironmentVariables)
        {
            if (Environment.GetEnvironmentVariable(name) is { } value)
            {
                startInfo.Environment[name] = value;
            }
        }

        foreach (var argument in Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new Process { StartInfo = startInfo };
    }
}

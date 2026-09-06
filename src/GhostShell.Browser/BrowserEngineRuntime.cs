using System.Diagnostics;
using Avalonia;
using Exclr8Cef;
using Exclr8Cef.WebView;
using GhostShell.Application;

namespace GhostShell.Browser;

/// <summary>
/// Owns the one process-wide CEF runtime used by all browser surfaces.
/// </summary>
public static class BrowserEngineRuntime
{
    private const string ExpectedCefVersion = "150.0.9";
    private const string ExpectedChromiumVersion = "150.0.7871.46";
    private const string ExpectedShimVersion = "0.8.0-ghostshell.7";
    internal const string DisabledChromiumFeatures =
        "OptimizationGuideOnDeviceModel,LogOnDeviceMetricsOnStartup";
    internal const string DisableChromeLoginPromptSwitch =
        "disable-chrome-login-prompt";
    private static readonly object StateGate = new();
    private static bool _initialized;
    private static bool _shutdown;

    /// <summary>
    /// Lets CEF claim renderer/GPU/utility subprocess invocations before any
    /// GhostSHELL single-instance, storage, or UI initialization occurs.
    /// </summary>
    public static int ExecuteSubprocess() => Cef.ExecuteProcess();

    /// <summary>
    /// Adds CEF's external message pump to the Avalonia application builder.
    /// </summary>
    public static AppBuilder Configure(AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseExclr8Cef();
    }

    /// <summary>
    /// Initializes CEF after Avalonia setup. On macOS this ordering is required
    /// so Avalonia's Objective-C classes exist before the CEF framework loads.
    /// </summary>
    public static void Initialize(BrowserEngineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (StateGate)
        {
            if (_initialized)
            {
                return;
            }

            if (_shutdown)
            {
                throw new InvalidOperationException(
                    "CEF cannot be initialized again after shutdown.");
            }

            PrepareProfileLayout(options.ProfileDirectory);
            PreparePrivateDirectory(
                Path.GetDirectoryName(options.LogFilePath)
                ?? throw new ArgumentException(
                    "The CEF log path must have a parent directory.",
                    nameof(options)));

            var helperPath = AvaloniaSetup.LocateMacHelper(
                "GhostSHELL Helper");
            if (OperatingSystem.IsMacOS() && helperPath is null)
            {
                throw new FileNotFoundException(
                    "The GhostSHELL CEF helper bundle is missing from "
                    + "Contents/Frameworks. Build or package a complete CEF runtime payload.");
            }

            var versions = Cef.GetVersions();
            ValidateVersions(versions);
            var settings = CreateSettings(options);
            // Chromium 150 can launch its unused on-device model service
            // through either of these feature gates. Disable both so its
            // startup metrics path cannot request a GPU adapter independently
            // of browser rendering.
            Cef.AddCommandLineSwitch(
                "disable-features",
                DisabledChromiumFeatures);
            // Chrome runtime otherwise owns HTTP authentication and shows its
            // login dialog instead of invoking CEF's GetAuthCredentials hook.
            // GhostSHELL answers that hook only for an authenticated workspace
            // proxy at the exact configured loopback endpoint.
            Cef.AddCommandLineSwitch(DisableChromeLoginPromptSwitch);
            if (GetMacOsSafeStorageSwitch(
                    OperatingSystem.IsMacOS()) is { } safeStorageSwitch)
            {
                Cef.AddCommandLineSwitch(safeStorageSwitch);
            }

            Cef.SetInitSettings(settings);

            // Environment.GetCommandLineArgs includes argv[0]. This matters on
            // Linux, where --type=renderer must not accidentally become argv[0].
            Cef.InitializeForOsr(
                Environment.GetCommandLineArgs(),
                helperPath,
                _ => { });
            _initialized = true;
        }
    }

    /// <summary>
    /// Closes every browser and continues pumping on the main thread until CEF
    /// confirms OnBeforeClose for each one, then performs process shutdown.
    /// </summary>
    /// <returns>False when browser close confirmation timed out.</returns>
    public static bool Shutdown(
        CefBrowserProfileStore? profileStore = null,
        TimeSpan? timeout = null)
    {
        lock (StateGate)
        {
            if (!_initialized || _shutdown)
            {
                return true;
            }

            var closeTimeout = timeout ?? TimeSpan.FromSeconds(10);
            if (closeTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            foreach (var browser in Cef.Browsers.ToArray())
            {
                browser.Close(force: true);
            }

            var elapsed = Stopwatch.StartNew();
            while (Cef.Browsers.Any() && elapsed.Elapsed < closeTimeout)
            {
                Cef.DoMessageLoopWork();
                Thread.Sleep(TimeSpan.FromMilliseconds(5));
            }

            if (Cef.Browsers.Any())
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "browser.shutdown.close-timeout",
                    SecretSafeDiagnosticKind.Timeout);
                return false;
            }

            var succeeded = true;
            try
            {
                profileStore?.ReleaseContextsForEngineShutdown();
            }
            catch (Exception exception)
                when (exception is IOException
                    or InvalidDataException
                    or InvalidOperationException)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "browser.shutdown.context-release-failed",
                    exception);
                succeeded = false;
            }

            try
            {
                Cef.Shutdown();
            }
            finally
            {
                _shutdown = true;
                _initialized = false;
            }

            try
            {
                if (profileStore?.SealRuntimeStateAfterEngineShutdown() == false)
                {
                    SecretSafeDiagnosticProjection.WriteStandardError(
                        "browser.shutdown.state-seal-failed",
                        SecretSafeDiagnosticKind.Unexpected);
                    succeeded = false;
                }
            }
            catch (Exception exception)
                when (exception is IOException
                    or InvalidDataException
                    or InvalidOperationException)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "browser.shutdown.state-seal-failed",
                    exception);
                succeeded = false;
            }

            return succeeded;
        }
    }

    internal static void ValidateVersions(CefVersions versions)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (!string.Equals(
                versions.Shim,
                ExpectedShimVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                versions.Cef,
                ExpectedCefVersion,
                StringComparison.Ordinal)
            || !string.Equals(
                versions.Chromium,
                ExpectedChromiumVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The staged CEF runtime does not match the managed binding. "
                + $"Expected shim {ExpectedShimVersion} / CEF "
                + $"{ExpectedCefVersion} / Chromium "
                + $"{ExpectedChromiumVersion}, found CEF {versions.Cef} / "
                + $"Chromium {versions.Chromium} / shim {versions.Shim}.");
        }
    }

    internal static Cef.CefSettings CreateSettings(
        BrowserEngineRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new Cef.CefSettings
        {
            // The global context remains unused. Durable user request contexts
            // receive child cache paths under this owner-private working root.
            CachePath = null,
            RootCachePath = options.ProfileDirectory,
            UserAgentProduct = $"GhostSHELL/{options.ProductVersion}",
            // The vendor callback cannot suppress Chromium's default console
            // emission. Disable native persistence and project warning/error
            // callbacks through CefConsoleMessagePolicy instead.
            LogFile = options.LogFilePath,
            LogSeverity = Cef.CefLogSeverity.Disable,
            PersistSessionCookies = true,
            RemoteDebuggingPort = 0,
        };
    }

    internal static string? GetMacOsSafeStorageSwitch(bool isMacOs)
    {
        if (!isMacOs)
        {
            return null;
        }

        // GhostSHELL restores Chromium into an owner-only runtime directory and
        // seals the complete tree with its application-encryption key after CEF
        // shuts down. Chromium's separate Safe Storage integration adds login-
        // keychain prompts without adding at-rest protection to the sealed blob.
        return "use-mock-keychain";
    }

    private static void PreparePrivateDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath))
        {
            throw new InvalidDataException(
                "A browser runtime directory is occupied by a file.");
        }

        Directory.CreateDirectory(fullPath);
        var info = new DirectoryInfo(fullPath);
        info.Refresh();
        if (info.LinkTarget is not null
            || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException(
                "The browser runtime root is an unexpected filesystem link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }

    internal static void PrepareProfileLayout(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        var root = Path.GetFullPath(rootDirectory);

        PreparePrivateDirectory(root);
    }
}

public sealed record BrowserEngineRuntimeOptions
{
    public BrowserEngineRuntimeOptions(
        string profileDirectory,
        string logFilePath,
        string productVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(productVersion);
        ProfileDirectory = Path.GetFullPath(profileDirectory);
        LogFilePath = Path.GetFullPath(logFilePath);
        ProductVersion = productVersion;
    }

    public string ProfileDirectory { get; }

    public string LogFilePath { get; }

    public string ProductVersion { get; }

}

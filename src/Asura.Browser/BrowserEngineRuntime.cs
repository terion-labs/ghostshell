using System.Diagnostics;
using Asura.Application;
using Avalonia;
using Exclr8Cef;
using Exclr8Cef.WebView;

namespace Asura.Browser;

/// <summary>
/// Owns the one process-wide CEF runtime used by all browser surfaces.
/// </summary>
public static class BrowserEngineRuntime
{
    private const string ExpectedCefVersion = "150.0.9";
    private const string ExpectedChromiumVersion = "150.0.7871.46";
    private const string ExpectedShimVersion = "0.8.0-asura.10";
    internal const string DisabledChromiumFeatures =
        "OptimizationGuideOnDeviceModel,LogOnDeviceMetricsOnStartup";
    internal const string DisableChromeLoginPromptSwitch =
        "disable-chrome-login-prompt";
    private static readonly object StateGate = new();
    private static bool _initialized;
    private static bool _shutdown;

    /// <summary>
    /// Lets CEF claim renderer/GPU/utility subprocess invocations before any
    /// Asura single-instance, storage, or UI initialization occurs.
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
                "Asura Helper");
            if (OperatingSystem.IsMacOS() && helperPath is null)
            {
                throw new FileNotFoundException(
                    "The Asura CEF helper bundle is missing from "
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
            // Asura answers that hook only for an authenticated workspace
            // proxy at the exact configured loopback endpoint.
            Cef.AddCommandLineSwitch(DisableChromeLoginPromptSwitch);
            // Use Chromium's real platform cookie encryption. The stock macOS
            // framework uses its shared Chromium Safe Storage Keychain item;
            // our separately keyed encrypted profile snapshot still protects
            // browser storage that OSCrypt does not encrypt. Never substitute
            // Chromium's public test key to suppress a Keychain prompt.

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

            return succeeded;
        }
    }

    /// <summary>
    /// Archives only after synchronous CEF shutdown has drained browser work.
    /// Snapshot preparation may await a bounded platform copy process without
    /// holding the CEF state lock or moving CEF shutdown off its owning thread.
    /// </summary>
    public static async Task<bool> SealStateAfterShutdownAsync(
        CefBrowserProfileStore profileStore,
        Func<string, string, CancellationToken, Task>? copyEngineSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        lock (StateGate)
        {
            if (!_shutdown || _initialized)
            {
                throw new InvalidOperationException("Browser state cannot be sealed before CEF shutdown.");
            }
        }

        try
        {
            if (!await profileStore.SealRuntimeStateAfterEngineShutdownAsync(copyEngineSnapshot, cancellationToken).ConfigureAwait(false))
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "browser.shutdown.state-seal-failed",
                    SecretSafeDiagnosticKind.Unexpected);
                return false;
            }
        }
        catch (Exception exception)
            when (exception is IOException
                or InvalidDataException
                or InvalidOperationException
                or OperationCanceledException)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "browser.shutdown.state-seal-failed",
                exception);
            return false;
        }

        return true;
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
            UserAgentProduct = $"Asura/{options.ProductVersion}",
            // The vendor callback cannot suppress Chromium's default console
            // emission. Disable native persistence and project warning/error
            // callbacks through CefConsoleMessagePolicy instead.
            LogFile = options.LogFilePath,
            LogSeverity = Cef.CefLogSeverity.Disable,
            PersistSessionCookies = true,
            RemoteDebuggingPort = 0,
        };
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

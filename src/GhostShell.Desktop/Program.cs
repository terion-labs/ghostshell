using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dock.Settings;
using GhostShell.App;
using GhostShell.App.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Application;
using GhostShell.Browser;
using GhostShell.Infrastructure;
using GhostShell.SessionHost;
using GhostShell.Terminal;
using GhostShell.Updates;
using Microsoft.Extensions.DependencyInjection;
using GhostShellApplication = GhostShell.App.App;

namespace GhostShell.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (WorkspaceSshCommand.IsInvocation(args))
        {
            Environment.ExitCode = WorkspaceSshCommand
                .RunAsync(args, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return;
        }

        if (WorkspaceSocksProxyCommand.IsInvocation(args))
        {
            Environment.ExitCode = WorkspaceSocksProxyCommand
                .RunAsync(args, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return;
        }

        VelopackStartup.Run(args);

        if (ConnectionCredentialProcessHost.IsPrivateHelperInvocation(args))
        {
            Environment.ExitCode = ConnectionCredentialProcessHost
                .TryRunAsync(args, CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult()
                ?? 1;
            return;
        }

        try
        {
            var cefExitCode = BrowserEngineRuntime.ExecuteSubprocess();
            if (cefExitCode >= 0)
            {
                Environment.ExitCode = cefExitCode;
                return;
            }
        }
        catch (Exception error)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.cef-subprocess.failed",
                error);
            Environment.ExitCode = 1;
            return;
        }

        if (args.Contains(
                BrowserNativeCheckboxProbe.CommandLineSwitch,
                StringComparer.Ordinal))
        {
            Environment.ExitCode = BrowserNativeCheckboxProbe.Run();
            return;
        }

        // macOS will only host the UI on the process's first thread, and an
        // async Main leaves it at the first await that does real work —
        // resolving an encryption key from the keychain, say. So this thread
        // never awaits: it waits the asynchronous preparation out, starts
        // the lifetime exactly where the platform demands it, then waits the
        // finalization out the same way. Private credential helpers have
        // already exited without loading CEF; normal runs and CEF --type
        // subprocesses preserve CEF's required first-dispatch ordering.
        var prepared = PrepareAsync().GetAwaiter().GetResult();
        if (prepared is StartupPreparation.Failed failure)
        {
            // Preparation resumes on worker threads. Avalonia, including an
            // error-only lifetime, must start on this original macOS thread.
            DesktopStartupFailurePresenter.TryShow(
                "GhostSHELL could not open this profile",
                failure.Message,
                args);
            Environment.ExitCode = 1;
            return;
        }
        if (prepared is not StartupPreparation.Ready ready)
        {
            return;
        }

        var (services, instanceCoordinator) = ready;
        var cefInitialized = false;
        MainWindowViewModel? mainWindowViewModel = null;
        INativeNotificationService? nativeNotifications = null;
        try
        {
            try
            {
                instanceCoordinator.RegisterActivationHandler(RequestMainWindowActivation);
                var lifetime = new ClassicDesktopStyleApplicationLifetime
                {
                    Args = args,
                    ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose,
                };
                var updateShutdown = services
                    .GetRequiredService<DesktopUpdateShutdown>();
                updateShutdown.Attach(
                    lifetime,
                    cancellationToken => services
                        .GetRequiredService<GhostShellApplication>()
                        .PrepareForUpdateRestartAsync(cancellationToken));
                BrowserEngineRuntime.Configure(BuildAvaloniaApp(services))
                    .SetupWithLifetime(lifetime);
                nativeNotifications =
                    services.GetRequiredService<INativeNotificationService>();
                nativeNotifications.Activated += OnNativeNotificationActivated;
                mainWindowViewModel = services.GetRequiredService<MainWindowViewModel>();
                lifetime.Exit += (_, _) =>
                    TeardownPresentationOrReport(mainWindowViewModel);
                void InitializeBrowserRuntime()
                {
                    try
                    {
                        BrowserEngineRuntime.Initialize(
                            CreateBrowserEngineOptions(services));
                        cefInitialized = true;
                    }
                    catch (Exception error)
                    {
                        SecretSafeDiagnosticProjection.WriteStandardError(
                            "desktop.cef-initialize.failed",
                            error);
                        throw;
                    }
                }

                var encryption = services
                    .GetRequiredService<ApplicationEncryptionRuntime>();
                if (encryption.AwaitingUnlock)
                {
                    DeferredStartupCoordinator.Arm(
                        services.GetRequiredService<IStartupProtection>(),
                        () => InitializeProfileCoreAsync(services),
                        InitializeBrowserRuntime);
                }
                else
                {
                    try
                    {
                        InitializeBrowserRuntime();
                    }
                    catch
                    {
                        Environment.ExitCode = 1;
                        return;
                    }
                }

                Environment.ExitCode = lifetime.Start(args);
                // Exit normally performs this while the dispatcher still pumps.
                // The process's STA thread is a safe fallback if the lifetime
                // returns without raising Exit.
                TeardownPresentationOrReport(mainWindowViewModel);
                FinalizeAsync(services).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "desktop.runtime.failed",
                    error);
                Environment.ExitCode = 1;
            }
            finally
            {
                services.GetRequiredService<DesktopUpdateShutdown>().Detach();
                instanceCoordinator.StopAcceptingActivations();
                nativeNotifications?.Activated -= OnNativeNotificationActivated;

                // Startup and finalization failures also converge here before
                // CEF closes browsers and stops its message pump.
                TeardownPresentationOrReport(mainWindowViewModel);
                QuiescePresentationOrReport(services);
                if (cefInitialized
                    && !BrowserEngineRuntime.Shutdown(
                        services.GetRequiredService<CefBrowserProfileStore>()))
                {
                    Environment.ExitCode = 1;
                }

                services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            instanceCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void TeardownPresentationOrReport(
        MainWindowViewModel? mainWindowViewModel)
    {
        if (mainWindowViewModel is null)
        {
            return;
        }

        try
        {
            mainWindowViewModel.TeardownPresentationForShutdown();
        }
        catch (Exception error)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.presentation-teardown.failed",
                error);
            Environment.ExitCode = 1;
        }
    }

    private static void QuiescePresentationOrReport(IServiceProvider services)
    {
        try
        {
            QuiescePresentationAsync(
                    services.GetRequiredService<QuickTerminalController>(),
                    services.GetRequiredService<GhostShellApplication>(),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception error)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.presentation-quiesce.failed",
                error);
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Everything that must happen before the window can exist, on whatever
    /// threads it needs. Null means an existing instance was activated. Errors
    /// are returned to Main for presentation on the process's original thread.
    /// </summary>
    private static async Task<StartupPreparation?> PrepareAsync()
    {
        ConfigureDockDiagnostics();

        var instanceStart = await SingleInstanceCoordinator.StartAsync(
            GhostShellDataPaths.CreateDefault().DataDirectory,
            CancellationToken.None);
        if (instanceStart is SingleInstanceStartResult.ExistingInstanceActivated)
        {
            return null;
        }
        if (instanceStart is SingleInstanceStartResult.Failure instanceFailure)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.startup.failed",
                SecretSafeDiagnosticKind.Unexpected);
            return new StartupPreparation.Failed(instanceFailure.Error.Message);
        }

        var instanceCoordinator =
            ((SingleInstanceStartResult.Primary)instanceStart).Coordinator;
        var services = DesktopComposition.CreateServiceProvider();
        try
        {
            // Before anything opens the configuration database: an encrypted
            // database needs its key in hand for the very first connection —
            // from the OS keystore, or, when protection sealed the keys under
            // the PIN, from the unlock that has not happened yet.
            var protection = services.GetRequiredService<IStartupProtection>()
                as StartupProtectionRuntime;
            var encryption = services.GetRequiredService<ApplicationEncryptionRuntime>();
            await encryption.InitializeAsync(
                wrappedKeysPending: protection?.HoldsWrappedKeys ?? false,
                CancellationToken.None);
            if (encryption.StartupError is { } encryptionError)
            {
                SecretSafeDiagnosticProjection.WriteStandardError(
                    "desktop.profile-open.failed",
                    SecretSafeDiagnosticKind.Unexpected);
                Abandon();
                return new StartupPreparation.Failed(encryptionError);
            }

            Task<string?> InitializeProfileAsync() => InitializeProfileCoreAsync(services);

            if (!encryption.AwaitingUnlock
                && await InitializeProfileAsync() is { } profileError)
            {
                Abandon();
                return new StartupPreparation.Failed(profileError);
            }

            return new StartupPreparation.Ready(services, instanceCoordinator);
        }
        catch
        {
            Abandon();
            throw;
        }

        void Abandon()
        {
            services.DisposeAsync().AsTask().GetAwaiter().GetResult();
            instanceCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private abstract record StartupPreparation
    {
        public sealed record Ready(
            ServiceProvider Services,
            SingleInstanceCoordinator Coordinator) : StartupPreparation;

        public sealed record Failed(string Message) : StartupPreparation;
    }

    private static async Task<string?> InitializeProfileCoreAsync(IServiceProvider services)
    {
        if (!services.GetRequiredService<CefBrowserProfileStore>()
                .RecoverOrphanedRuntimeState())
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.browser-profile-recovery.failed",
                SecretSafeDiagnosticKind.Unexpected);
            return "Saved browser sessions could not be recovered safely.";
        }

        var runStore = services.GetRequiredService<IApplicationRunStore>();
        var startResult = await runStore.BeginRunAsync(CancellationToken.None);
        if (!startResult.IsSuccess)
        {
            ReportLifecycleFailure(
                "initialize its recovery marker",
                startResult.Error!);
            return $"Local application data is unavailable ({startResult.Error!.Code}).";
        }

        var startupState = services.GetRequiredService<ApplicationStartupState>();
        startupState.Initialize(startResult.Value!);

        var agentAuditRecovery = await services
            .GetRequiredService<AgentAuditRecovery>()
            .RecoverAsync(CancellationToken.None);
        if (!agentAuditRecovery.IsSuccess)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.agent-audit-recovery.failed",
                SecretSafeDiagnosticKind.Unexpected);
            return "The local agent audit trail is unavailable or invalid.";
        }

        var catalog = services.GetRequiredService<IDefinitionCatalog>();
        var catalogResult = await catalog.InitializeAsync(CancellationToken.None);
        if (!catalogResult.IsSuccess)
        {
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.definition-catalog.load-failed",
                SecretSafeDiagnosticKind.Unexpected);
            return $"Saved connections and workspaces are unavailable "
                + $"({catalogResult.Error!.Code}).";
        }

        // Failure-tolerant by design: unreadable settings mean the
        // defaults, never a startup error.
        await services.GetRequiredService<SqliteFilePreviewPreferences>()
            .InitializeAsync(CancellationToken.None);
        await services.GetRequiredService<SqliteBrowserProfilePreferences>()
            .InitializeAsync(CancellationToken.None);
        await services.GetRequiredService<AgentPolicyCoordinator>()
            .InitializeAsync(CancellationToken.None);
        startupState.MarkProfileInitialized();
        return null;
    }

    private static async Task FinalizeAsync(ServiceProvider services)
    {
        // The run began either before the lifetime or, with sealed keys,
        // behind the lock screen; quitting at the lock screen means no run
        // marker was ever written and there is nothing to finalize.
        if (services.GetRequiredService<ApplicationStartupState>().Run is not { } run)
        {
            return;
        }

        var mainWindowViewModel = services.GetRequiredService<MainWindowViewModel>();
        var application = services.GetRequiredService<GhostShellApplication>();
        // The desktop dispatcher no longer pumps once the classic lifetime returns.
        var completion = await services.GetRequiredService<DesktopRunFinalizer>()
            .FinalizeAsync(
                cancellationToken => QuiescePresentationAsync(
                    services.GetRequiredService<QuickTerminalController>(),
                    application,
                    cancellationToken),
                mainWindowViewModel.FlushRecentSessionHistoryAsync,
                _ => services.GetRequiredService<InMemorySessionHostClient>().DisposeAsync(),
                run.RunId,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!completion.IsSuccess)
        {
            ReportLifecycleFailure(
                "finalize its recovery state",
                completion.Error!);
        }
    }

    private static void ConfigureDockDiagnostics()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("GHOSTSHELL_DOCK_DIAGNOSTICS"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        DockSettings.EnableDiagnosticsLogging = true;
        DockSettings.DiagnosticsLogHandler = _ =>
            SecretSafeDiagnosticProjection.WriteStandardError(
                "desktop.dock-diagnostic",
                SecretSafeDiagnosticKind.Unexpected);
    }

    internal static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder
            .Configure(() => services.GetRequiredService<GhostShellApplication>())
            .UsePlatformDetect()
            .WithInterFont()
            .ConfigureFonts(fontManager =>
                fontManager.AddFontCollection(new GhostShellTerminalFontCollection()))
            .SetDragPreviewOpacity(0.9);

    private static BrowserEngineRuntimeOptions CreateBrowserEngineOptions(
        IServiceProvider services)
    {
        var artifacts = LocalArtifactPaths.CreateDefault();
        var browserPaths = services.GetRequiredService<BrowserProfileStoragePaths>();
        var version = typeof(Program).Assembly.GetName().Version;
        return new BrowserEngineRuntimeOptions(
            browserPaths.RuntimeDirectory,
            Path.Combine(artifacts.ApplicationLogDirectory, "cef.log"),
            version is null ? "0.0.0" : version.ToString(3));
    }

    private static void ReportLifecycleFailure(
        string operation,
        ApplicationRunError error)
    {
        _ = operation;
        _ = error;
        SecretSafeDiagnosticProjection.WriteStandardError(
            "desktop.lifecycle.failed",
            SecretSafeDiagnosticKind.Unexpected);
        Environment.ExitCode = 1;
    }

    private static void RequestMainWindowActivation()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is not IClassicDesktopStyleApplicationLifetime
                    {
                        MainWindow: { } mainWindow,
                    })
            {
                return;
            }

            if (mainWindow.WindowState == WindowState.Minimized)
            {
                mainWindow.WindowState = WindowState.Normal;
            }

            if (!mainWindow.IsVisible)
            {
                mainWindow.Show();
            }

            mainWindow.Activate();
        });
    }

    private static void OnNativeNotificationActivated(
        object? sender,
        NativeNotificationActivatedEventArgs eventArgs)
    {
        _ = sender;
        _ = eventArgs;
        RequestMainWindowActivation();
    }

    private static async Task QuiescePresentationAsync(
        QuickTerminalController quickTerminalController,
        GhostShellApplication application,
        CancellationToken cancellationToken)
    {
        quickTerminalController.Dispose();
        await application.QuiesceForShutdownAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

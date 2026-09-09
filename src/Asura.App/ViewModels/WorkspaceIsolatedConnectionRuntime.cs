using Asura.Application;
using Asura.Core;

namespace Asura.App.ViewModels;

/// <summary>
/// Rewrites terminal process plans so the host PTY starts the workspace
/// isolation runtime instead of the connection executable directly.
/// </summary>
internal interface IWorkspaceIsolationTerminalRuntime
{
}

internal sealed class WorkspaceIsolatedConnectionRuntime :
    IConnectionRuntime,
    IConnectionCommandRuntime,
    IWorkspaceIsolationTerminalRuntime
{
    private readonly IConnectionRuntime _inner;
    private readonly IWorkspaceIsolationProvider _provider;
    private readonly WorkspaceIsolationBinding _binding;

    public WorkspaceIsolatedConnectionRuntime(
        IConnectionRuntime inner,
        IWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _binding = binding ?? throw new ArgumentNullException(nameof(binding));
    }

    public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (RejectBeforeHostPlanning(profile) is { } rejected)
        {
            return rejected;
        }

        var result = await _inner.PlanOpenAsync(profile, progress, cancellationToken)
            .ConfigureAwait(false);
        return Rewrite(profile, result);
    }

    public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (RejectBeforeHostPlanning(profile) is { } rejected)
        {
            return rejected;
        }

        var result = await _inner.PlanOpenAsync(
                profile,
                multiplexerSession,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return Rewrite(profile, result);
    }

    private static ConnectionRuntimeResult<ConnectionOpenPlan>? RejectBeforeHostPlanning(
        ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ConnectionKind is ConnectionKind.Docker or ConnectionKind.Wsl)
        {
            return Fail(WorkspaceIsolationError.Create(
                WorkspaceIsolationErrorCode.UnsupportedConnectionKind));
        }

        if (profile.Authentication is ConnectionAuthentication.Password
                or ConnectionAuthentication.PrivateKey
            || profile.Startup.Environment.Any(variable =>
                variable.Value is ConnectionEnvironmentValue.Secret))
        {
            return Fail(WorkspaceIsolationError.Create(
                WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable));
        }

        return null;
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        _ = profile;
        _ = progress;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Fail(
            new ConnectionRuntimeError(
                ConnectionRuntimeErrorCode.UnsupportedPlatform,
                "workspace_isolation_test_unavailable",
                "Connection tests are unavailable until they can run inside the workspace isolate.",
                Retryable: false,
                ConnectionRecoveryAction.None)));
    }

    public async ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await PlanCommandAsync(
            connection,
            executable,
            arguments,
            WorkspaceProcessMode.None,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await PlanCommandAsync(
            connection,
            executable,
            arguments,
            WorkspaceProcessMode.Interactive,
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
        ConnectionProfile connection,
        string executable,
        IReadOnlyList<string> arguments,
        WorkspaceProcessMode mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (RejectBeforeHostPlanning(connection) is { } rejected)
        {
            return ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                ((ConnectionRuntimeResult<ConnectionOpenPlan>.Failure)rejected).Error);
        }

        var planned = await _inner.PlanOpenAsync(
                connection,
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (planned is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure failure)
        {
            return ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(failure.Error);
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)planned).Value;
        var commandArguments = CommandArguments(plan, executable, arguments);
        var isolated = _provider.CreateExecLaunch(
            _binding,
            new WorkspaceIsolationProcessRequest(
                plan.Kind,
                plan.Kind == ConnectionKind.Local
                    ? executable
                    : plan.Launch.Executable
                        ?? throw new InvalidOperationException(
                            "The connection plan has no executable."),
                commandArguments,
                plan.Launch.Environment,
                plan.Kind == ConnectionKind.Local && connection.Startup.Directory is null
                    ? null
                    : plan.Launch.WorkingDirectory,
                mode,
                usesHostCredentialBroker: plan.IsSecretBrokerPrepared));
        return isolated switch
        {
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success success =>
                ConnectionRuntimeResult<TerminalLaunchRequest>.Succeed(
                    new TerminalLaunchRequest(
                        success.Value.HostWorkingDirectory,
                        success.Value.Executable,
                        success.Value.Arguments,
                        success.Value.Environment,
                        connectionId: connection.Id)),
            WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure isolationFailure =>
                ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                    new ConnectionRuntimeError(
                        MapErrorCode(isolationFailure.Error.Code),
                        isolationFailure.Error.StableCode,
                        isolationFailure.Error.Message,
                        isolationFailure.Error.Retryable,
                        MapRecoveryAction(isolationFailure.Error.RecoveryAction))),
            _ => throw new InvalidOperationException(
                "The workspace isolation provider returned an unknown result."),
        };
    }

    private static IReadOnlyList<string> CommandArguments(
        ConnectionOpenPlan plan,
        string executable,
        IReadOnlyList<string> arguments)
    {
        if (plan.Kind == ConnectionKind.Local)
        {
            return arguments;
        }

        if (plan.Kind != ConnectionKind.Ssh)
        {
            throw new InvalidOperationException(
                $"The {plan.Kind} connection cannot run inside this workspace isolate.");
        }

        // This is the App-layer counterpart to ConnectionCommandExecutor's SSH command
        // encoding; App cannot depend on its Infrastructure implementation.
        var boundary = -1;
        for (var index = 0; index < plan.Launch.Arguments.Count; index++)
        {
            if (string.Equals(plan.Launch.Arguments[index], "--", StringComparison.Ordinal))
            {
                boundary = index;
                break;
            }
        }

        if (boundary < 0 || boundary + 1 >= plan.Launch.Arguments.Count)
        {
            throw new InvalidOperationException("The SSH connection plan is malformed.");
        }

        var sshArguments = plan.Launch.Arguments
            .Take(boundary)
            .Where(argument => !string.Equals(argument, "-tt", StringComparison.Ordinal))
            .ToList();
        sshArguments.Add(plan.Launch.Arguments[boundary]);
        sshArguments.Add(plan.Launch.Arguments[boundary + 1]);
        var remoteCommand = new[] { executable }
            .Concat(arguments)
            .Select(QuotePosixShellWord);
        sshArguments.Add(string.Join(' ', remoteCommand));
        return Array.AsReadOnly(sshArguments.ToArray());
    }

    private static string QuotePosixShellWord(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private ConnectionRuntimeResult<ConnectionOpenPlan> Rewrite(
        ConnectionProfile profile,
        ConnectionRuntimeResult<ConnectionOpenPlan> result)
    {
        if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure)
        {
            return result;
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)result).Value;
        if (string.IsNullOrWhiteSpace(plan.Launch.Executable))
        {
            return Fail(WorkspaceIsolationError.Create(
                WorkspaceIsolationErrorCode.ExecutableMappingUnavailable));
        }

        var isolated = _provider.CreateExecLaunch(
            _binding,
            new WorkspaceIsolationProcessRequest(
                plan.Kind,
                plan.Launch.Executable,
                plan.Launch.Arguments,
                plan.Launch.Environment,
                plan.Kind == ConnectionKind.Local && profile.Startup.Directory is null
                    ? null
                    : plan.Launch.WorkingDirectory,
                WorkspaceProcessMode.Interactive | WorkspaceProcessMode.AllocateTerminal,
                usesHostCredentialBroker: plan.IsSecretBrokerPrepared));
        if (isolated is WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure failure)
        {
            return Fail(failure.Error);
        }

        var process = ((WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success)isolated).Value;
        // The host PTY observes the long-lived `container exec` wrapper, not the guest
        // shell's foreground process. Use the conservative visible-prompt classifier for
        // local guest shells when native semantic shell state is therefore unavailable.
        var shellActivityFallback = plan.Kind == ConnectionKind.Local
            ? TerminalShellActivityFallback.PromptShape
            : plan.Launch.ShellActivityFallback;
        var launch = new TerminalLaunchRequest(
            process.HostWorkingDirectory,
            process.Executable,
            process.Arguments,
            process.Environment,
            plan.Launch.RenderProfile,
            plan.Launch.Keymap,
            plan.Launch.ConnectionId,
            plan.Launch.ConnectionMetadata,
            plan.Launch.InitialCommand,
            shellActivityFallback,
            plan.Launch.MultiplexerSession);
        return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
            new ConnectionOpenPlan(
                plan.ConnectionId,
                plan.Kind,
                launch,
                plan.Authentication,
                plan.HostKeyPolicy,
                plan.ReconnectMode,
                plan.SecretRequirements,
                plan.Warnings,
                plan.IsSecretBrokerPrepared));
    }

    private static ConnectionRuntimeResult<ConnectionOpenPlan> Fail(
        WorkspaceIsolationError error) =>
        ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(new ConnectionRuntimeError(
            MapErrorCode(error.Code),
            error.StableCode,
            error.Message,
            error.Retryable,
            MapRecoveryAction(error.RecoveryAction)));

    private static ConnectionRuntimeErrorCode MapErrorCode(
        WorkspaceIsolationErrorCode code) => code switch
        {
            WorkspaceIsolationErrorCode.RuntimeMissing
                or WorkspaceIsolationErrorCode.RuntimeVersionTooOld =>
                ConnectionRuntimeErrorCode.RuntimeMissing,
            WorkspaceIsolationErrorCode.WorkingDirectoryNotMounted =>
                ConnectionRuntimeErrorCode.InvalidProfile,
            WorkspaceIsolationErrorCode.PersistentEnvironmentResetRequired =>
                ConnectionRuntimeErrorCode.InvalidProfile,
            WorkspaceIsolationErrorCode.UnsupportedConnectionKind
                or WorkspaceIsolationErrorCode.ExecutableMappingUnavailable
                or WorkspaceIsolationErrorCode.SshHostKeyTrustUnavailable
                or WorkspaceIsolationErrorCode.ImageNotBootable =>
                ConnectionRuntimeErrorCode.UnsupportedPlatform,
            WorkspaceIsolationErrorCode.HostCredentialBrokerUnavailable =>
                ConnectionRuntimeErrorCode.AuthenticationRequired,
            WorkspaceIsolationErrorCode.Cancelled => ConnectionRuntimeErrorCode.Cancelled,
            WorkspaceIsolationErrorCode.Timeout => ConnectionRuntimeErrorCode.Timeout,
            WorkspaceIsolationErrorCode.RuntimeUnavailable
                or WorkspaceIsolationErrorCode.PrepareFailed
                or WorkspaceIsolationErrorCode.StopFailed =>
                ConnectionRuntimeErrorCode.ProcessFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        };

    private static ConnectionRecoveryAction MapRecoveryAction(
        WorkspaceIsolationRecoveryAction action) => action switch
        {
            WorkspaceIsolationRecoveryAction.InstallRuntime
                or WorkspaceIsolationRecoveryAction.UpdateRuntime =>
                ConnectionRecoveryAction.InstallRuntime,
            WorkspaceIsolationRecoveryAction.StartRuntime
                or WorkspaceIsolationRecoveryAction.Retry =>
                ConnectionRecoveryAction.Retry,
            WorkspaceIsolationRecoveryAction.ChooseMountedDirectory =>
                ConnectionRecoveryAction.EditProfile,
            WorkspaceIsolationRecoveryAction.ResetPersistentEnvironment =>
                ConnectionRecoveryAction.EditProfile,
            WorkspaceIsolationRecoveryAction.None
                or WorkspaceIsolationRecoveryAction.DisableIsolation =>
                ConnectionRecoveryAction.None,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
}

internal sealed class WorkspaceIsolationUnavailableConnectionRuntime :
    IConnectionRuntime,
    IWorkspaceIsolationTerminalRuntime
{
    public static WorkspaceIsolationUnavailableConnectionRuntime ScopeChanged { get; } = new(
        "workspace_isolation_scope_changed",
        "Workspace isolation changed while this workspace was open. Close and reopen it before starting another terminal.");

    public static WorkspaceIsolationUnavailableConnectionRuntime BindingMissing { get; } = new(
        "workspace_isolation_binding_missing",
        "The workspace isolation binding is unavailable. Close and reopen the workspace before starting another terminal.");

    private readonly ConnectionRuntimeError _error;

    private WorkspaceIsolationUnavailableConnectionRuntime(string stableCode, string message)
    {
        _error = new ConnectionRuntimeError(
            ConnectionRuntimeErrorCode.ProcessFailed,
            stableCode,
            message,
            Retryable: false,
            ConnectionRecoveryAction.None);
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(_error));
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken) =>
        PlanOpenAsync(profile, progress, cancellationToken);

    public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Fail(_error));
    }
}

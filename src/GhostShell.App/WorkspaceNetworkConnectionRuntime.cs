using GhostShell.Application;
using GhostShell.Core;

namespace GhostShell.App;

internal sealed class WorkspaceNetworkConnectionRuntime(
    IConnectionRuntime inner,
    WorkspaceNetworkEgressState egressState,
    bool injectProxyEnvironment) : IConnectionRuntime
{
    public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await inner.PlanOpenAsync(profile, progress, cancellationToken)
            .ConfigureAwait(false);
        return Apply(profile, result);
    }

    public async ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
        ConnectionProfile profile,
        TerminalMultiplexerSession? multiplexerSession,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await inner.PlanOpenAsync(
                profile,
                multiplexerSession,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        return Apply(profile, result);
    }

    public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
        ConnectionProfile profile,
        IProgress<ConnectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        var egress = egressState.Current;
        if (egress == WorkspaceNetworkEgress.Direct)
        {
            return inner.TestAsync(profile, progress, cancellationToken);
        }

        _ = progress;
        return ValueTask.FromResult(ConnectionRuntimeResult<ConnectionTestReport>.Fail(
            egress == WorkspaceNetworkEgress.Blocked
                ? new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.Offline,
                    "workspace_network_kill_switch_blocked",
                    "The workspace network kill switch is blocking traffic.",
                    Retryable: true,
                    ConnectionRecoveryAction.Reconnect)
                : new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.UnsupportedPlatform,
                    "workspace_network_route_test_unavailable",
                    "Connection tests are unavailable until they can use the active workspace network route.",
                    Retryable: false,
                    ConnectionRecoveryAction.None)));
    }

    private ConnectionRuntimeResult<ConnectionOpenPlan> Apply(
        ConnectionProfile profile,
        ConnectionRuntimeResult<ConnectionOpenPlan> result)
    {
        if (result is ConnectionRuntimeResult<ConnectionOpenPlan>.Failure)
        {
            return result;
        }

        var egress = egressState.Current;
        if (egress == WorkspaceNetworkEgress.Blocked)
        {
            return ConnectionRuntimeResult<ConnectionOpenPlan>.Fail(
                new ConnectionRuntimeError(
                    ConnectionRuntimeErrorCode.Offline,
                    "workspace_network_kill_switch_blocked",
                    "The workspace network kill switch is blocking traffic.",
                    Retryable: true,
                    ConnectionRecoveryAction.Reconnect));
        }

        if (!injectProxyEnvironment)
        {
            return result;
        }

        var plan = ((ConnectionRuntimeResult<ConnectionOpenPlan>.Success)result).Value;
        var launch = plan.Launch;
        var proxy = egressState.LocalProxyEndpoint ?? egress.ProxyEndpoint;
        if (proxy is null)
        {
            return result;
        }

        var environment = new Dictionary<string, string>(launch.Environment, StringComparer.Ordinal);
        if (profile.Endpoint is ConnectionEndpoint.Local
            && Environment.ProcessPath is { } processPath)
        {
            // Git's SSH transport does not honor HTTP/ALL_PROXY. The desktop
            // wrapper resolves SSH aliases before installing its ProxyCommand.
            environment["GIT_SSH_COMMAND"] = $"{QuoteShellWord(processPath)} --ghostshell-workspace-ssh";
            environment["GIT_SSH_VARIANT"] = "ssh";
        }
        var arguments = launch.Arguments;
        if (profile.Endpoint is ConnectionEndpoint.Ssh ssh
            && WorkspaceSshProxyCommand.TryCreate(proxy, ssh, out var proxyCommand))
        {
            arguments = ["-o", $"ProxyCommand={proxyCommand}", .. arguments];
        }
        var routedLaunch = new TerminalLaunchRequest(
            launch.WorkingDirectory,
            launch.Executable,
            arguments,
            WorkspaceProxyEnvironment.Create(
                environment, proxy, egressState.LocalProxyCredentials, egressState.BrowserProxyEndpoint),
            launch.RenderProfile,
            launch.Keymap,
            launch.ConnectionId,
            launch.ConnectionMetadata,
            launch.InitialCommand,
            launch.ShellActivityFallback,
            launch.MultiplexerSession);
        return ConnectionRuntimeResult<ConnectionOpenPlan>.Succeed(
            new ConnectionOpenPlan(
                plan.ConnectionId,
                plan.Kind,
                routedLaunch,
                plan.Authentication,
                plan.HostKeyPolicy,
                plan.ReconnectMode,
                plan.SecretRequirements,
                plan.Warnings,
                plan.IsSecretBrokerPrepared));
    }

    private static string QuoteShellWord(string value) =>
        OperatingSystem.IsWindows() ? $"\"{value}\""
            : $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static class WorkspaceSshProxyCommand
    {
        public const string Switch = "--ghostshell-workspace-socks-connect";

        public static bool TryCreate(
            Uri proxy,
            ConnectionEndpoint.Ssh endpoint,
            out string command)
        {
            command = string.Empty;
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }

            var encodedHost = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(endpoint.Host));
            command = $"{QuoteShellWord(processPath)} "
                + $"{Switch} {proxy.Port} {encodedHost} {endpoint.Port}";
            return true;
        }
    }
}

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using GhostShell.Application;
using GhostShell.Core;
using GhostShell.Desktop;
using GhostShell.Infrastructure;

namespace GhostShell.Architecture.Tests;

public sealed class WorkspaceIsolationSocksProxyNativeTests
{
    private const string EnableVariable = "GHOSTSHELL_RUN_APPLE_CONTAINER_NATIVE";

    [NativeAppleContainerFact]
    public async Task Proxy_carries_a_real_TLS_request_through_the_workspace_isolate()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var workspaceId = new GhostShell.Core.WorkspaceId(
            $"native-browser-{Guid.NewGuid():N}");
        var request = new WorkspaceIsolationPrepareRequest(workspaceId);
        var provider = new AppleContainerWorkspaceIsolationProvider();
        WorkspaceIsolationBinding? binding = null;
        try
        {
            binding = Success(await provider.PrepareAsync(request, timeout.Token));
            await using var proxy = new WorkspaceIsolationSocksProxy(
                new ProviderCommandRuntime(provider, binding),
                BuiltInConnections.Local);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, proxy.LocalPort, timeout.Token);
            var stream = client.GetStream();

            await stream.WriteAsync(new byte[] { 5, 1, 2 }, timeout.Token);
            var greeting = new byte[2];
            await stream.ReadExactlyAsync(greeting, timeout.Token);
            Assert.Equal(new byte[] { 5, 2 }, greeting);
            var username = Encoding.UTF8.GetBytes(proxy.LocalProxyCredentials.Username);
            var password = Encoding.UTF8.GetBytes(proxy.LocalProxyCredentials.Password);
            var authentication = new byte[3 + username.Length + password.Length];
            authentication[0] = 1;
            authentication[1] = checked((byte)username.Length);
            username.CopyTo(authentication, 2);
            authentication[2 + username.Length] = checked((byte)password.Length);
            password.CopyTo(authentication, 3 + username.Length);
            await stream.WriteAsync(authentication, timeout.Token);
            var authenticationReply = new byte[2];
            await stream.ReadExactlyAsync(authenticationReply, timeout.Token);
            Assert.Equal(new byte[] { 1, 0 }, authenticationReply);

            const string host = "www.google.com";
            var hostBytes = Encoding.ASCII.GetBytes(host);
            var connect = new byte[7 + hostBytes.Length];
            connect[0] = 5;
            connect[1] = 1;
            connect[2] = 0;
            connect[3] = 3;
            connect[4] = checked((byte)hostBytes.Length);
            hostBytes.CopyTo(connect, 5);
            connect[^2] = 1;
            connect[^1] = 187;
            await stream.WriteAsync(connect, timeout.Token);
            var reply = new byte[10];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            Assert.Equal((byte)0, reply[1]);

            using var tls = new SslStream(stream, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                },
                timeout.Token);
            await tls.WriteAsync(
                "HEAD / HTTP/1.1\r\nHost: www.google.com\r\nConnection: close\r\n\r\n"u8
                    .ToArray(),
                timeout.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII);
            var statusLine = await reader.ReadLineAsync(timeout.Token);

            Assert.StartsWith("HTTP/1.1 ", statusLine, StringComparison.Ordinal);
        }
        finally
        {
            if (binding is not null)
            {
                _ = await provider.StopAsync(binding, CancellationToken.None);
            }

            _ = await provider.RecreateAsync(request, progress: null, CancellationToken.None);
        }
    }

    private static T Success<T>(WorkspaceIsolationResult<T> result) =>
        Assert.IsType<WorkspaceIsolationResult<T>.Success>(result).Value;

    private sealed class ProviderCommandRuntime(
        IWorkspaceIsolationProvider provider,
        WorkspaceIsolationBinding binding) : IConnectionCommandRuntime
    {
        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var launch = provider.CreateExecLaunch(
                binding,
                new WorkspaceIsolationProcessRequest(
                    connection.ConnectionKind,
                    executable,
                    arguments));
            return ValueTask.FromResult(launch switch
            {
                WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success success =>
                    ConnectionRuntimeResult<TerminalLaunchRequest>.Succeed(new TerminalLaunchRequest(
                        success.Value.HostWorkingDirectory,
                        success.Value.Executable,
                        success.Value.Arguments,
                        success.Value.Environment,
                        connectionId: connection.Id)),
                WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure failure =>
                    ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(new ConnectionRuntimeError(
                        ConnectionRuntimeErrorCode.ProcessFailed,
                        failure.Error.StableCode,
                        failure.Error.Message,
                        failure.Error.Retryable,
                    ConnectionRecoveryAction.None)),
                _ => throw new InvalidOperationException(),
            });
        }

        public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PlanDuplexCommandAsync(
            ConnectionProfile connection,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var launch = provider.CreateExecLaunch(
                binding,
                new WorkspaceIsolationProcessRequest(
                    connection.ConnectionKind,
                    executable,
                    arguments,
                    mode: WorkspaceProcessMode.Interactive));
            return ValueTask.FromResult(launch switch
            {
                WorkspaceIsolationResult<WorkspaceProcessLaunch>.Success success =>
                    ConnectionRuntimeResult<TerminalLaunchRequest>.Succeed(new TerminalLaunchRequest(
                        success.Value.HostWorkingDirectory,
                        success.Value.Executable,
                        success.Value.Arguments,
                        success.Value.Environment,
                        connectionId: connection.Id)),
                WorkspaceIsolationResult<WorkspaceProcessLaunch>.Failure failure =>
                    ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(new ConnectionRuntimeError(
                        ConnectionRuntimeErrorCode.ProcessFailed,
                        failure.Error.StableCode,
                        failure.Error.Message,
                        failure.Error.Retryable,
                        ConnectionRecoveryAction.None)),
                _ => throw new InvalidOperationException(),
            });
        }
    }

    private sealed class NativeAppleContainerFactAttribute : FactAttribute
    {
        public NativeAppleContainerFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(EnableVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {EnableVariable}=1 to exercise the installed Apple container runtime.";
                return;
            }

            if (!OperatingSystem.IsMacOS()
                || RuntimeInformation.ProcessArchitecture
                    != System.Runtime.InteropServices.Architecture.Arm64)
            {
                Skip = "The Apple container native test requires Apple-silicon macOS.";
            }
        }
    }
}

using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

public sealed class ConnectionProbeErrorMapperTests
{
    public static TheoryData<ConnectionProbeResult, ConnectionKind, ConnectionRuntimeErrorCode>
        Mappings => new()
        {
            {
                new ConnectionProbeResult(ConnectionProbeOutcome.Cancelled, null, string.Empty),
                ConnectionKind.Ssh,
                ConnectionRuntimeErrorCode.Cancelled
            },
            {
                new ConnectionProbeResult(ConnectionProbeOutcome.TimedOut, null, string.Empty),
                ConnectionKind.Ssh,
                ConnectionRuntimeErrorCode.Timeout
            },
            {
                new ConnectionProbeResult(
                    ConnectionProbeOutcome.StartFailed,
                    null,
                    string.Empty,
                    ConnectionProbeStartFailure.NotFound),
                ConnectionKind.Docker,
                ConnectionRuntimeErrorCode.RuntimeMissing
            },
            {
                new ConnectionProbeResult(
                    ConnectionProbeOutcome.StartFailed,
                    null,
                    string.Empty,
                    ConnectionProbeStartFailure.PermissionDenied),
                ConnectionKind.Docker,
                ConnectionRuntimeErrorCode.PermissionDenied
            },
            {
                new ConnectionProbeResult(
                    ConnectionProbeOutcome.StartFailed,
                    null,
                    string.Empty,
                    ConnectionProbeStartFailure.Unknown),
                ConnectionKind.Docker,
                ConnectionRuntimeErrorCode.ProcessFailed
            },
            {
                Failed("WARNING: REMOTE HOST IDENTIFICATION HAS CHANGED!"),
                ConnectionKind.Ssh,
                ConnectionRuntimeErrorCode.HostKeyChanged
            },
            {
                Failed("Host key verification failed."),
                ConnectionKind.Ssh,
                ConnectionRuntimeErrorCode.UnknownHostKey
            },
            {
                Failed("Permission denied (publickey,password)."),
                ConnectionKind.Ssh,
                ConnectionRuntimeErrorCode.AuthenticationFailed
            },
            {
                Failed("Error response from daemon: No such container: removed"),
                ConnectionKind.Docker,
                ConnectionRuntimeErrorCode.ContainerNotFound
            },
            {
                Failed("permission denied while trying to connect to the Docker daemon socket"),
                ConnectionKind.Docker,
                ConnectionRuntimeErrorCode.PermissionDenied
            },
            {
                Failed("Wsl/Service/WSL_E_DISTRO_NOT_FOUND"),
                ConnectionKind.Wsl,
                ConnectionRuntimeErrorCode.DistributionNotFound
            },
            {
                Failed("Access is denied."),
                ConnectionKind.Wsl,
                ConnectionRuntimeErrorCode.PermissionDenied
            },
            {
                Failed("connect to host example port 22: Connection timed out"),
                ConnectionKind.Ssh,
                ConnectionRuntimeErrorCode.Timeout
            },
            {
                Failed("ssh: Could not resolve hostname example"),
                ConnectionKind.Ssh,
                ConnectionRuntimeErrorCode.Offline
            },
            {
                Failed("Cannot connect to the Docker daemon at unix:///var/run/docker.sock"),
                ConnectionKind.Docker,
                ConnectionRuntimeErrorCode.Offline
            },
            {
                Failed("unclassified failure"),
                ConnectionKind.Wsl,
                ConnectionRuntimeErrorCode.ProcessFailed
            },
        };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Maps_process_outcomes_without_returning_process_text(
        ConnectionProbeResult result,
        ConnectionKind kind,
        ConnectionRuntimeErrorCode expectedCode)
    {
        var error = ConnectionProbeErrorMapper.Map(result, kind);

        Assert.Equal(expectedCode, error.Code);
        if (result.StandardError.Length > 0)
        {
            Assert.DoesNotContain(result.StandardError, error.Message, StringComparison.Ordinal);
        }

        Assert.StartsWith("connection_", error.StableCode, StringComparison.Ordinal);
    }

    private static ConnectionProbeResult Failed(string standardError) =>
        new(ConnectionProbeOutcome.Exited, 1, standardError);
}

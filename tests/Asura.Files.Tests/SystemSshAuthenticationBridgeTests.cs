using Asura.Application;
using Asura.Core;
using Renci.SshNet;

namespace Asura.Files.Tests;

public sealed class SystemSshAuthenticationBridgeTests
{
    [Fact]
    public async Task EmptyAgentIsPreparedThroughSharedConnectionTransport()
    {
        var connection = RemoteProviderTestProfiles.SftpOptions().Connection;
        var identitySource = new QueueIdentitySource(
            [],
            IdentityBatch());
        var runtime = new RecordingConnectionRuntime(
            ConnectionRuntimeResult<ConnectionTestReport>.Succeed(
                new ConnectionTestReport(
                    connection.Id,
                    ConnectionKind.Ssh,
                    ConnectionTestVerification.EndpointAuthenticated,
                    endpointReached: true)));
        var bridge = new SystemSshAuthenticationBridge(
            connection,
            runtime,
            identitySource);

        var identities = await bridge.GetIdentitiesAsync(CancellationToken.None);

        Assert.Single(identities);
        Assert.Same(connection, Assert.Single(runtime.TestRequests));
        Assert.Equal(2, identitySource.ReadCount);
    }

    [Fact]
    public async Task ExistingAgentIdentityDoesNotOpenPreparationConnection()
    {
        var identitySource = new QueueIdentitySource(IdentityBatch());
        var runtime = new RecordingConnectionRuntime(
            ConnectionRuntimeResult<ConnectionTestReport>.Fail(
                ConnectionRuntimeError.Create(
                    ConnectionRuntimeErrorCode.AuthenticationFailed)));
        var bridge = new SystemSshAuthenticationBridge(
            RemoteProviderTestProfiles.SftpOptions().Connection,
            runtime,
            identitySource);

        var identities = await bridge.GetIdentitiesAsync(CancellationToken.None);

        Assert.Single(identities);
        Assert.Empty(runtime.TestRequests);
        Assert.Equal(1, identitySource.ReadCount);
    }

    [Fact]
    public async Task FailedTransportPreparationRemainsAuthenticationFailure()
    {
        var bridge = new SystemSshAuthenticationBridge(
            RemoteProviderTestProfiles.SftpOptions().Connection,
            new RecordingConnectionRuntime(
                ConnectionRuntimeResult<ConnectionTestReport>.Fail(
                    ConnectionRuntimeError.Create(
                        ConnectionRuntimeErrorCode.AuthenticationFailed))),
            new QueueIdentitySource(Array.Empty<IPrivateKeySource>()));

        var exception = await Assert.ThrowsAsync<RemoteFileSessionException>(
            async () => await bridge.GetIdentitiesAsync(CancellationToken.None));

        Assert.Equal(RemoteFileSessionErrorCode.AuthenticationFailed, exception.Code);
    }

    [Theory]
    [InlineData(
        ConnectionRuntimeErrorCode.UnknownHostKey,
        (int)RemoteFileSessionErrorCode.HostKeyUnknown,
        false)]
    [InlineData(
        ConnectionRuntimeErrorCode.HostKeyChanged,
        (int)RemoteFileSessionErrorCode.HostKeyChanged,
        false)]
    [InlineData(
        ConnectionRuntimeErrorCode.Offline,
        (int)RemoteFileSessionErrorCode.Transient,
        true)]
    public async Task TransportPreparationFailurePreservesConnectionClassification(
        ConnectionRuntimeErrorCode transportError,
        int expectedErrorValue,
        bool retryable)
    {
        var bridge = new SystemSshAuthenticationBridge(
            RemoteProviderTestProfiles.SftpOptions().Connection,
            new RecordingConnectionRuntime(
                ConnectionRuntimeResult<ConnectionTestReport>.Fail(
                    ConnectionRuntimeError.Create(transportError))),
            new QueueIdentitySource(Array.Empty<IPrivateKeySource>()));

        var exception = await Assert.ThrowsAsync<RemoteFileSessionException>(
            async () => await bridge.GetIdentitiesAsync(CancellationToken.None));

        Assert.Equal((RemoteFileSessionErrorCode)expectedErrorValue, exception.Code);
        Assert.Equal(retryable, exception.Retryable);
    }

    private static IPrivateKeySource[] IdentityBatch() => [null!];

    private sealed class QueueIdentitySource(
        params IPrivateKeySource[][] responses) : ISshAgentIdentitySource
    {
        private readonly Queue<IPrivateKeySource[]> _responses = new(responses);

        public int ReadCount { get; private set; }

        public ValueTask<IPrivateKeySource[]> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(_responses.Dequeue());
        }
    }

    private sealed class RecordingConnectionRuntime(
        ConnectionRuntimeResult<ConnectionTestReport> testResult) : IConnectionRuntime
    {
        public List<ConnectionProfile> TestRequests { get; } = [];

        public ValueTask<ConnectionRuntimeResult<ConnectionOpenPlan>> PlanOpenAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Authentication preparation must use diagnostics.");

        public ValueTask<ConnectionRuntimeResult<ConnectionTestReport>> TestAsync(
            ConnectionProfile profile,
            IProgress<ConnectionProgress>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TestRequests.Add(profile);
            return ValueTask.FromResult(testResult);
        }
    }
}

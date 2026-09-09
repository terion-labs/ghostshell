using System.Reflection;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class TerminalStartupCommandDispatcherTests
{
    [Fact]
    public async Task HostCancellationProducesASeparateTypedCancelledAuditOutcome()
    {
        var audit = new RecordingAuditStore();
        var client = DispatchProxy.Create<ISessionHostClient, TerminalWriteClient>();
        var proxy = (TerminalWriteClient)(object)client;
        proxy.Result = HostResult<Unit>.Fail(
            HostError.Create(HostErrorCode.Cancelled, "Cancelled."),
            currentRevision: 3);
        var dispatcher = new TerminalStartupCommandDispatcher(audit, TimeProvider.System);
        var context = OperationContext.ForHuman(new ClientId("startup-client"));

        var result = await dispatcher.DispatchAsync(
            client,
            new SessionId("startup-session"),
            new InputLeaseId("startup-lease"),
            ["secret-canary-command"],
            context,
            CancellationToken.None);

        Assert.Equal(TerminalStartupCommandDispatchErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal([AuditOutcome.Started, AuditOutcome.Cancelled], audit.Events.Select(item => item.Outcome));
        var cancelled = Assert.IsType<AuditDetails.TerminalStartupCommandDetails>(
            audit.Events[^1].Details);
        Assert.Equal(TerminalStartupCommandDispatchErrorCode.Cancelled, cancelled.ErrorCode);
        Assert.DoesNotContain(
            audit.Events,
            item => item.ToString()!.Contains("secret-canary-command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailableAuditTrailFailsClosedBeforeTerminalWrite()
    {
        var audit = new RecordingAuditStore
        {
            Failure = new AuditStoreError(
                AuditStoreErrorCode.StorageUnavailable,
                "Unavailable."),
        };
        var client = DispatchProxy.Create<ISessionHostClient, TerminalWriteClient>();
        var proxy = (TerminalWriteClient)(object)client;
        proxy.Result = HostResult<Unit>.Succeed(Unit.Value, 1);
        var dispatcher = new TerminalStartupCommandDispatcher(audit, TimeProvider.System);

        var result = await dispatcher.DispatchAsync(
            client,
            new SessionId("startup-session"),
            new InputLeaseId("startup-lease"),
            ["deploy"],
            OperationContext.ForHuman(new ClientId("startup-client")),
            CancellationToken.None);

        Assert.False(result.CommandsDelivered);
        Assert.Equal(
            TerminalStartupCommandDispatchErrorCode.AuditPersistenceFailure,
            result.Error!.Code);
        Assert.Equal(0, proxy.WriteCalls);
    }

    public class TerminalWriteClient : DispatchProxy
    {
        public HostResult<Unit> Result { get; set; } = null!;

        public int WriteCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            _ = args;
            if (!string.Equals(targetMethod?.Name, nameof(ISessionHostClient.WriteTerminalAsync), StringComparison.Ordinal))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            WriteCalls++;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        public List<AuditEventRecord> Events { get; } = [];

        public AuditStoreError? Failure { get; init; }

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return ValueTask.FromResult(AuditStoreResult<Unit>.Failure(Failure));
            }

            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(auditEvent);
            return ValueTask.FromResult(AuditStoreResult<Unit>.Success(Unit.Value));
        }

        public ValueTask<AuditStoreResult<IReadOnlyList<AuditEventRecord>>>
            ListByCorrelationAsync(
                string correlationId,
                CancellationToken cancellationToken) =>
            ValueTask.FromResult(AuditStoreResult<IReadOnlyList<AuditEventRecord>>.Success(
                [.. Events.Where(item => string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal))]));
    }
}

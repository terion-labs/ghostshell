using System.Reflection;
using Asura.Application;
using Asura.Core;

namespace Asura.SessionHost.Tests;

public sealed class TerminalStartupCommandDispatchIntegrationTests
{
    [Fact]
    public async Task LostAcknowledgementRetriesTheSameBatchWithoutRepeatingTerminalInput()
    {
        await using var harness = new SessionHostTestHarness();
        await harness.OpenAsync();
        var lease = (await harness.Client.AcquireInputLeaseAsync(
            new AcquireInputLeaseRequest(harness.SessionId, null, TimeSpan.FromMinutes(5)),
            harness.HumanContext(),
            CancellationToken.None)).Value().Lease!;
        var transport = DispatchProxy.Create<ISessionHostClient, LostAcknowledgementClient>();
        var proxy = (LostAcknowledgementClient)(object)transport;
        proxy.Inner = harness.Client;
        var audit = new RecordingAuditStore();
        var dispatcher = new TerminalStartupCommandDispatcher(audit, harness.Clock);
        var context = harness.HumanContext(
            idempotencyKey: new IdempotencyKey("saved-screen-startup-batch"));
        var commands = new[] { "deploy --environment production" };

        var unknown = await dispatcher.DispatchAsync(
            transport,
            harness.SessionId,
            lease.Id,
            commands,
            context,
            CancellationToken.None);
        var replayed = await dispatcher.DispatchAsync(
            transport,
            harness.SessionId,
            lease.Id,
            commands,
            context,
            CancellationToken.None);

        Assert.Equal(
            TerminalStartupCommandDispatchErrorCode.WriteOutcomeUnknown,
            unknown.Error!.Code);
        Assert.True(unknown.Error.Retryable);
        Assert.True(replayed.Succeeded);
        Assert.Equal(1, harness.Factory[harness.SessionId].WriteCount);
        Assert.Equal("deploy --environment production\n", harness.Factory[harness.SessionId].LastWrittenText);
        Assert.Equal(2, proxy.WriteCalls);
        Assert.Equal(
            [AuditOutcome.Started, AuditOutcome.Failed, AuditOutcome.Started, AuditOutcome.Succeeded],
            audit.Events.Select(item => item.Outcome));
        Assert.All(audit.Events, auditEvent =>
        {
            Assert.Equal(context.RequestId.Value, auditEvent.CorrelationId);
            var details = Assert.IsType<AuditDetails.TerminalStartupCommandDetails>(
                auditEvent.Details);
            Assert.Equal(1, details.CommandCount);
        });
    }

    public class LostAcknowledgementClient : DispatchProxy
    {
        public ISessionHostClient Inner { get; set; } = null!;

        public int WriteCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (!string.Equals(targetMethod?.Name, nameof(ISessionHostClient.WriteTerminalAsync)
, StringComparison.Ordinal) || args is not
                [TerminalWriteRequest request, OperationContext context, CancellationToken token])
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            return WriteAsync(request, context, token);
        }

        private async ValueTask<HostResult<Unit>> WriteAsync(
            TerminalWriteRequest request,
            OperationContext context,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            var result = await Inner.WriteTerminalAsync(
                request,
                context,
                cancellationToken);
            if (WriteCalls == 1)
            {
                throw new IOException("The response frame was lost after acceptance.");
            }

            return result;
        }
    }

    private sealed class RecordingAuditStore : IAuditStore
    {
        public List<AuditEventRecord> Events { get; } = [];

        public ValueTask<AuditStoreResult<Unit>> AppendAsync(
            AuditEventRecord auditEvent,
            CancellationToken cancellationToken)
        {
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

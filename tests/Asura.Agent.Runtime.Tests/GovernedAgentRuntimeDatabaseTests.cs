using System.Collections.Concurrent;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeProcessTests
{
    [Fact]
    public async Task RedisIndexDiscoveryIsIncludedInTheWorkspaceManifest()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactDatabase,
            ScriptedProvider.AnswerOnly(),
            DatabasePolicy(AgentPermission.Auto));
        fixture.Context.IncludeRedisIndexCapabilities = true;

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Inspect the Redis database."),
            CancellationToken.None);

        Assert.True(result.IsSuccess, $"{result.Code}: {result.Message}");
        var request = Assert.Single(fixture.Provider.Requests);
        var system = Assert.Single(
            request.Messages,
            message => message.Role == AgentMessageRole.System);
        Assert.Contains(
            "operations=\"read_state,redis_list_indexes,redis_search\"",
            system.Content,
            StringComparison.Ordinal);
        Assert.Contains(
            request.Tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.RedisListIndexes, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutoDatabaseObservationIsReachableThroughSendAsync()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactDatabase,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.DatabaseReadState,
                "{}"),
            DatabasePolicy(AgentPermission.Auto));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Read the database session state."),
            CancellationToken.None);

        Assert.True(
            result.IsSuccess,
            $"{result.Code}: {result.Message}; {fixture.Runtime.Snapshot.Status}; "
            + $"actions={fixture.Database.Actions.Count}; requests={fixture.Provider.Requests.Count}");
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(1, fixture.Database.CallCount);
        Assert.Equal(
            ProcessRuntimeContextProxy.DatabasePanelId,
            Assert.Single(fixture.Database.Actions).Request.PanelId);
        var initial = fixture.Provider.Requests.ToArray()[0];
        var tool = Assert.Single(
            initial.Tools,
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.DatabaseReadState, StringComparison.Ordinal));
        Assert.Empty(tool.InputSchema.GetProperty("properties").EnumerateObject());
        var system = Assert.Single(
            initial.Messages,
            message => message.Role == AgentMessageRole.System);
        Assert.Contains("database_count=1", system.Content, StringComparison.Ordinal);
        Assert.Contains("docker_count=0", system.Content, StringComparison.Ordinal);
        var contextItem = Assert.Single(fixture.Runtime.Snapshot.ContextItems);
        Assert.Equal(PanelKind.DatabaseViewer, contextItem.Kind);
        Assert.Contains(
            BuiltInAgentTools.DatabaseReadState,
            contextItem.SupportedOperations, StringComparer.Ordinal);

        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal(AgentToolResultStatus.Succeeded, toolResult.Status);
        Assert.Equal("database_state_read", toolResult.StableCode);
        using var document = JsonDocument.Parse(toolResult.Value.Content);
        Assert.Equal(
            DatabaseAgentToolResultJson.ContentOrigin,
            document.RootElement.GetProperty("content_origin").GetString());
        Assert.Equal(
            "relational",
            document.RootElement.GetProperty("backend").GetString());
        Assert.Contains(
            fixture.Audit.Events,
            auditEvent => string.Equals(auditEvent.Action, BuiltInAgentTools.DatabaseReadState
, StringComparison.Ordinal) && auditEvent.Outcome == AuditOutcome.Succeeded);
    }

    private static AgentPolicy DatabasePolicy(AgentPermission permission) =>
        AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.DatabaseRead,
                permission),
        };

    private sealed class ConsumingDatabaseHost(
        IAgentCapabilityBroker broker,
        AgentDatabaseReadActionComposer composer,
        ProcessRuntimeContextProxy context)
        : IAgentDatabaseSessionHost
    {
        private int _callCount;

        public ConcurrentQueue<AgentDatabaseReadAction> Actions { get; } = [];

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<HostResult<AgentDatabaseReadResult>>
            RunAgentDatabaseReadAsync(
                AgentAuthorizationId authorizationId,
                AgentDatabaseReadAction action,
                CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var binding = composer.BindForExecution(
                action,
                context.ExactDatabaseContext(action.Proposal.Target));
            var consumed = await broker.ConsumeAsync(
                authorizationId,
                binding,
                cancellationToken);
            if (consumed is AgentPermitResult.Denied denied)
            {
                return HostResult<AgentDatabaseReadResult>.Fail(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        denied.Error.Code.ToString().ToLowerInvariant(),
                        "The database authorization was denied."),
                    1);
            }

            var permit = ((AgentPermitResult.Granted)consumed).Permit;
            Actions.Enqueue(action);
            var projected = composer.Project(
                action,
                new DatabasePanelSessionState(
                    DatabasePanelBackend.Relational,
                    "sqlite",
                    "SQLite",
                    IsReady: true,
                    ServerVersion: "3.50"));
            var completion = await broker.CompleteAsync(
                permit,
                new AgentActionCompletion(
                    AgentActionOutcome.Succeeded,
                    "database_read_completed",
                    DateTimeOffset.UtcNow,
                    resultCount: 1),
                CancellationToken.None);
            if (completion is not null)
            {
                return HostResult<AgentDatabaseReadResult>.Fail(
                    new HostError(
                        HostErrorCode.EngineFailed,
                        AgentActionFailureCodes.CompletionAuditUnavailable,
                        "The database completion audit is unresolved."),
                    1);
            }

            return HostResult<AgentDatabaseReadResult>.Succeed(projected, 1);
        }
    }
}

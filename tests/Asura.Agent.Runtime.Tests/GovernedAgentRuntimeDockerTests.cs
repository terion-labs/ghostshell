using System.Collections.Concurrent;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;
using Asura.Docker;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeProcessTests
{
    [Fact]
    public async Task AutoDockerObservationIsReachableThroughSendAsync()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactDocker,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.DockerReadState,
                "{}"),
            DockerPolicy(AgentPermission.Auto));

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Read the Docker engine state."),
            CancellationToken.None);

        Assert.True(
            result.IsSuccess,
            $"{result.Code}: {result.Message}; {fixture.Runtime.Snapshot.Status}");
        Assert.Equal(1, fixture.Docker.CallCount);
        Assert.Equal(
            ProcessRuntimeContextProxy.DockerPanelId,
            Assert.Single(fixture.Docker.Actions).Request.PanelId);
        var initial = fixture.Provider.Requests.ToArray()[0];
        Assert.Contains(
            initial.Tools,
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.DockerReadState, StringComparison.Ordinal));
        var system = Assert.Single(
            initial.Messages,
            message => message.Role == AgentMessageRole.System);
        Assert.Contains("database_count=0", system.Content, StringComparison.Ordinal);
        Assert.Contains("docker_count=1", system.Content, StringComparison.Ordinal);
        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal("docker_state_read", toolResult.StableCode);
        using var document = JsonDocument.Parse(toolResult.Value.Content);
        Assert.Equal(
            DockerAgentToolResultJson.ContentOrigin,
            document.RootElement.GetProperty("content_origin").GetString());
    }

    private static AgentPolicy DockerPolicy(AgentPermission permission) =>
        AgentPolicy.Default with
        {
            Permissions = AgentPolicy.Default.Permissions.SetItem(
                AgentCapability.DockerData,
                permission),
        };

    private sealed class ConsumingDockerHost(
        IAgentCapabilityBroker broker,
        AgentDockerReadActionComposer composer,
        ProcessRuntimeContextProxy context)
        : IAgentDockerSessionHost
    {
        private int _callCount;

        public ConcurrentQueue<AgentDockerReadAction> Actions { get; } = [];

        public int CallCount => Volatile.Read(ref _callCount);

        public async ValueTask<HostResult<AgentDockerReadResult>>
            RunAgentDockerReadAsync(
                AgentAuthorizationId authorizationId,
                AgentDockerReadAction action,
                CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            var binding = composer.BindForExecution(
                action,
                context.ExactDockerContext(action.Proposal.Target));
            var consumed = await broker.ConsumeAsync(
                authorizationId,
                binding,
                cancellationToken);
            if (consumed is AgentPermitResult.Denied denied)
            {
                return HostResult<AgentDockerReadResult>.Fail(
                    new HostError(
                        HostErrorCode.InvalidRequest,
                        denied.Error.Code.ToString().ToLowerInvariant(),
                        "The Docker authorization was denied."),
                    1);
            }

            var permit = ((AgentPermitResult.Granted)consumed).Permit;
            Actions.Enqueue(action);
            var projected = composer.Project(
                action,
                new DockerEngineGeneration("engine_generation_1"),
                new DockerPanelSnapshot(
                    new DockerEngineSummary("28", "Linux", "amd64", "1.51"),
                    [],
                    [],
                    [],
                    [],
                    DateTimeOffset.UnixEpoch,
                    IsTruncated: false));
            var completion = await broker.CompleteAsync(
                permit,
                new AgentActionCompletion(
                    AgentActionOutcome.Succeeded,
                    "docker_read_completed",
                    DateTimeOffset.UtcNow,
                    resultCount: 0),
                CancellationToken.None);
            if (completion is not null)
            {
                return HostResult<AgentDockerReadResult>.Fail(
                    new HostError(
                        HostErrorCode.EngineFailed,
                        AgentActionFailureCodes.CompletionAuditUnavailable,
                        "The Docker completion audit is unresolved."),
                    1);
            }

            return HostResult<AgentDockerReadResult>.Succeed(projected, 1);
        }
    }
}

using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed partial class GovernedAgentRuntimeProcessTests
{
    [Fact]
    public async Task AutoStatisticsObservationUsesBrokerHostAndContinuesProvider()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactStatistics,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.StatisticsRead,
                "{}"),
            StatisticsPolicy(AgentPermission.Auto));
        var statistics = Assert.IsType<ConsumingStatisticsHost>(
            fixture.Statistics);
        statistics.Snapshot = new SystemStatisticsSnapshot(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromHours(2),
            LogicalProcessorCount: 8,
            EnumeratedProcessCount: 41,
            ObservedProcessCount: 39,
            ObservedCpuPercent: 15.5,
            ObservedWorkingSetBytes: 8_192,
            NetworkReceivedBytesPerSecond: 100,
            NetworkSentBytesPerSecond: 50);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Show local system statistics."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, fixture.Provider.Requests.Count);
        Assert.Equal(1, statistics.CallCount);
        Assert.Equal(0, fixture.Processes.CallCount);
        var action = Assert.Single(statistics.Actions);
        Assert.Equal(
            ProcessRuntimeContextProxy.StatisticsPanelId,
            action.Request.PanelId);

        var initial = fixture.Provider.Requests.ToArray()[0];
        var tool = Assert.Single(
            initial.Tools,
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.StatisticsRead, StringComparison.Ordinal));
        Assert.Empty(tool.InputSchema.GetProperty("properties").EnumerateObject());
        Assert.DoesNotContain("panel_id", tool.InputSchema.GetRawText());
        Assert.DoesNotContain(
            initial.Tools,
            candidate => string.Equals(candidate.Name, BuiltInAgentTools.ProcessesList, StringComparison.Ordinal));
        var system = Assert.Single(
            initial.Messages,
            message => message.Role == AgentMessageRole.System);
        Assert.Contains("statistics_count=1", system.Content, StringComparison.Ordinal);
        Assert.Contains("process_count=0", system.Content, StringComparison.Ordinal);

        var contextItem = Assert.Single(fixture.Runtime.Snapshot.ContextItems);
        Assert.Equal(PanelKind.Statistics, contextItem.Kind);
        Assert.Equal(
            BuiltInAgentTools.StatisticsRead,
            Assert.Single(contextItem.SupportedOperations));
        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal(AgentToolResultStatus.Succeeded, toolResult.Status);
        Assert.Equal("statistics_read", toolResult.StableCode);
        using var document = JsonDocument.Parse(toolResult.Value.Content);
        Assert.Equal(
            StatisticsAgentToolResultJson.ContentOrigin,
            document.RootElement.GetProperty("content_origin").GetString());
        Assert.Equal(
            15.5,
            document.RootElement.GetProperty("observed_cpu_percent").GetDouble());
        Assert.Equal(
            8_192,
            document.RootElement.GetProperty("observed_working_set_bytes").GetInt64());
        foreach (var forbidden in new[]
                 {
                     "process_name",
                     "command_line",
                     "database",
                     "docker",
                 })
        {
            Assert.DoesNotContain(
                forbidden,
                toolResult.Value.Content,
                StringComparison.OrdinalIgnoreCase);
        }

        var completed = Assert.Single(
            fixture.Audit.Events,
            auditEvent => string.Equals(auditEvent.Action, BuiltInAgentTools.StatisticsRead
, StringComparison.Ordinal) && auditEvent.Outcome == AuditOutcome.Succeeded);
        var details = Assert.IsType<AuditDetails.AgentActionDetails>(
            completed.Details);
        Assert.Equal(AgentCapability.SystemData, details.Capability);
        Assert.Equal(AgentActionRisk.Observation, details.Risk);
        Assert.Equal(1, details.Binding.ResultCount);
    }

    [Fact]
    public async Task AskStatisticsDenialNeverCallsHost()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactStatistics,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.StatisticsRead,
                "{}"),
            StatisticsPolicy(AgentPermission.Ask));
        var statistics = Assert.IsType<ConsumingStatisticsHost>(
            fixture.Statistics);

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Read local statistics."),
            CancellationToken.None).AsTask();
        var approval = await WaitForApprovalAsync(fixture.Runtime);

        Assert.Equal(BuiltInAgentTools.StatisticsRead, approval.ToolName);
        Assert.Equal(AgentActionRisk.Observation, approval.Risk);
        Assert.False(approval.TemporarilyYieldsTerminalInput);
        Assert.Equal(
            "Local System Statistics",
            approval.Presentation.TargetTitle);
        Assert.Equal(
            [("panel_id", ProcessRuntimeContextProxy.StatisticsPanelId.Value)],
            approval.Presentation.Arguments.Select(argument =>
                (argument.Name, argument.DisplayValue)));
        _ = await fixture.Runtime.DecideAsync(
            approval.Id,
            approved: false,
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, statistics.CallCount);
        Assert.Equal(
            "approval_denied",
            ToolResultFromLastRequest(fixture.Provider).StableCode);
    }

    [Fact]
    public async Task OffStatisticsPolicyFailsClosedWithoutCallingHost()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactStatistics,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.StatisticsRead,
                "{}"),
            StatisticsPolicy(AgentPermission.Off));
        var statistics = Assert.IsType<ConsumingStatisticsHost>(
            fixture.Statistics);

        var result = await fixture.Runtime.SendAsync(
            fixture.Prompt("Read local statistics."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, statistics.CallCount);
        Assert.Equal(
            "policy_denied",
            ToolResultFromLastRequest(fixture.Provider).StableCode);
    }

    [Fact]
    public async Task BroadStatisticsScopeRequiresExplicitEligiblePanelId()
    {
        await using var omitted = ProcessRuntimeFixture.Create(
            ProcessScope.MixedStatisticsOpenTab,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.StatisticsRead,
                "{}"),
            StatisticsPolicy(AgentPermission.Auto));
        var omittedStatistics = Assert.IsType<ConsumingStatisticsHost>(
            omitted.Statistics);

        var omittedResult = await omitted.Runtime.SendAsync(
            omitted.Prompt("Read this tab's statistics."),
            CancellationToken.None);

        Assert.True(omittedResult.IsSuccess);
        Assert.Equal(0, omittedStatistics.CallCount);
        Assert.Equal(
            "invalid_tool_arguments",
            ToolResultFromLastRequest(omitted.Provider).StableCode);
        var schema = omitted.Provider.Requests.ToArray()[0].Tools
            .Single(tool => string.Equals(tool.Name, BuiltInAgentTools.StatisticsRead, StringComparison.Ordinal))
            .InputSchema;
        Assert.Equal(
            [ProcessRuntimeContextProxy.StatisticsPanelId.Value],
            schema.GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);

        await using var selected = ProcessRuntimeFixture.Create(
            ProcessScope.MixedStatisticsOpenTab,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.StatisticsRead,
                $$"""{"panel_id":"{{ProcessRuntimeContextProxy.StatisticsPanelId.Value}}"}"""),
            StatisticsPolicy(AgentPermission.Auto));
        var selectedStatistics = Assert.IsType<ConsumingStatisticsHost>(
            selected.Statistics);

        var selectedResult = await selected.Runtime.SendAsync(
            selected.Prompt("Read this tab's statistics."),
            CancellationToken.None);

        Assert.True(selectedResult.IsSuccess);
        Assert.Equal(1, selectedStatistics.CallCount);
        using var toolJson = JsonDocument.Parse(
            ToolResultFromLastRequest(selected.Provider).Value.Content);
        Assert.Equal(
            ProcessRuntimeContextProxy.StatisticsPanelId.Value,
            toolJson.RootElement.GetProperty("panel_id").GetString());
    }

    [Fact]
    public async Task ActiveStatisticsCaptureCanBeCancelledWithoutStoppingRun()
    {
        await using var fixture = ProcessRuntimeFixture.Create(
            ProcessScope.ExactStatistics,
            ScriptedProvider.ToolThenAnswer(
                BuiltInAgentTools.StatisticsRead,
                "{}"),
            StatisticsPolicy(AgentPermission.Auto));
        var statistics = Assert.IsType<ConsumingStatisticsHost>(
            fixture.Statistics);
        statistics.BlockAfterAuthorization = true;

        var sending = fixture.Runtime.SendAsync(
            fixture.Prompt("Read local statistics."),
            CancellationToken.None).AsTask();
        await statistics.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cancellation = await fixture.Runtime.CancelActiveActionAsync(
            CancellationToken.None);
        var result = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellation.WasRequested);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, statistics.CallCount);
        var toolResult = ToolResultFromLastRequest(fixture.Provider);
        Assert.Equal("caller_cancelled", toolResult.StableCode);
        Assert.Equal(AgentToolResultStatus.Failed, toolResult.Status);
    }
}

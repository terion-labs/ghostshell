using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class StatisticsAgentToolResultJsonTests
{
    [Fact]
    public void ProjectionContainsOnlyAggregateStatisticsFields()
    {
        var result = ProjectResult(new SystemStatisticsSnapshot(
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromHours(2),
            LogicalProcessorCount: 8,
            EnumeratedProcessCount: 50,
            ObservedProcessCount: 47,
            ObservedCpuPercent: 12.5,
            ObservedWorkingSetBytes: 8_192,
            NetworkReceivedBytesPerSecond: 1_500.25,
            NetworkSentBytesPerSecond: 750.5));
        var panelId = new PanelInstanceId("statistics-panel");

        var projection = StatisticsAgentToolResultJson.Project(
            result,
            panelId);

        Assert.True(projection.IsSuccess);
        Assert.Equal("statistics_read", projection.StableCode);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(panelId.Value, root.GetProperty("panel_id").GetString());
        Assert.Equal(
            StatisticsAgentToolResultJson.ContentOrigin,
            root.GetProperty("content_origin").GetString());
        Assert.Equal(7_200, root.GetProperty("host_uptime_seconds").GetDouble());
        Assert.Equal(8, root.GetProperty("logical_processor_count").GetInt32());
        Assert.Equal(50, root.GetProperty("enumerated_process_count").GetInt32());
        Assert.Equal(47, root.GetProperty("observed_process_count").GetInt32());
        Assert.Equal(12.5, root.GetProperty("observed_cpu_percent").GetDouble());
        Assert.Equal(8_192, root.GetProperty("observed_working_set_bytes").GetInt64());
        Assert.Equal(
            1_500.25,
            root.GetProperty("network_received_bytes_per_second").GetDouble());
        Assert.Equal(
            750.5,
            root.GetProperty("network_sent_bytes_per_second").GetDouble());
        foreach (var forbidden in ForbiddenFields)
        {
            Assert.DoesNotContain(
                forbidden,
                projection.Json,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void UnknownCpuAndNetworkRatesRemainExplicitNulls()
    {
        var result = ProjectResult(new SystemStatisticsSnapshot(
            DateTimeOffset.UnixEpoch,
            TimeSpan.Zero,
            LogicalProcessorCount: 1,
            EnumeratedProcessCount: 0,
            ObservedProcessCount: 0,
            ObservedCpuPercent: null,
            ObservedWorkingSetBytes: 0));

        var projection = StatisticsAgentToolResultJson.Project(result);

        using var document = JsonDocument.Parse(projection.Json);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("observed_cpu_percent").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement
                .GetProperty("network_received_bytes_per_second").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement
                .GetProperty("network_sent_bytes_per_second").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("panel_id", out _));
    }

    [Theory]
    [InlineData(HostErrorCode.InvalidRequest, "target_changed")]
    [InlineData(HostErrorCode.NotFound, "target_changed")]
    [InlineData(HostErrorCode.RevisionConflict, "target_changed")]
    [InlineData(HostErrorCode.UnsupportedProtocol, "statistics_unavailable")]
    [InlineData(HostErrorCode.CapabilityNotSupported, "statistics_unavailable")]
    [InlineData(HostErrorCode.SessionClosed, "statistics_unavailable")]
    [InlineData(HostErrorCode.DeadlineExceeded, "deadline_exceeded")]
    [InlineData(HostErrorCode.Cancelled, "cancelled")]
    [InlineData(HostErrorCode.EngineFailed, "statistics_capture_failed")]
    public void HostFailuresMapToClosedSecretFreeCodes(
        HostErrorCode code,
        string expected)
    {
        var error = new HostError(
            code,
            "provider-secret-code",
            "password=secret-canary",
            Retryable: true);

        var stableCode = StatisticsAgentToolResultJson.ProviderStableCode(error);
        var json = StatisticsAgentToolResultJson.Failure(error);

        Assert.Equal(expected, stableCode);
        Assert.DoesNotContain("provider-secret-code", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-canary", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("authority_revoked")]
    [InlineData("caller_cancelled")]
    public void KnownCancellationCodesRemainDistinguishable(string stableCode)
    {
        var error = new HostError(
            HostErrorCode.Cancelled,
            stableCode,
            "Sensitive cancellation detail.");

        Assert.Equal(
            stableCode,
            StatisticsAgentToolResultJson.ProviderStableCode(error));
    }

    private static AgentStatisticsReadResult ProjectResult(
        SystemStatisticsSnapshot snapshot)
    {
        var context = StatisticsContext();
        var panelId = context.Panels[0].PanelId;
        var now = DateTimeOffset.UtcNow;
        var composer = new AgentStatisticsReadActionComposer();
        var action = composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                new AgentRunId("statistics-json-run"),
                new ActorDescriptor(
                    new ActorId("statistics-json-agent"),
                    ActorKind.Agent,
                    "Statistics JSON agent"),
                policyGeneration: 1,
                createdAtUtc: now,
                deadlineUtc: now.AddMinutes(1)),
            context,
            new AgentStatisticsReadRequest(panelId));
        return composer.Project(action, snapshot);
    }

    private static AgentContextSnapshot StatisticsContext()
    {
        var windowId = new WindowInstanceId("statistics-json-window");
        var workspaceId = new WorkspaceInstanceId("statistics-json-workspace");
        var tabId = new TabInstanceId("statistics-json-tab");
        var panelId = new PanelInstanceId("statistics-panel");
        var sessionId = new SessionId("statistics-json-session");
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(
                workspaceId,
                "Statistics JSON workspace",
                [new TabInstance(
                    tabId,
                    "Statistics JSON tab",
                    [new PanelInstance(
                        panelId,
                        PanelKind.Statistics,
                        "Statistics",
                        sessionId)],
                    panelId)],
                tabId),
            revision: 1,
            lastSequence: 1);
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.Statistics,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            new CapabilitySet([SessionCapabilities.StatisticsRead]),
            Revision: 1,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return new AgentContextSnapshot(
            new AgentTarget.Panel(windowId, workspaceId, tabId, panelId),
            [AgentContextPanel.ForGraphPanel(
                graph,
                tabId,
                panelId,
                descriptor)],
            DateTimeOffset.UtcNow);
    }

    private static readonly string[] ForbiddenFields =
    [
        "process_name",
        "pid",
        "command_line",
        "database",
        "docker",
        "environment",
    ];
}

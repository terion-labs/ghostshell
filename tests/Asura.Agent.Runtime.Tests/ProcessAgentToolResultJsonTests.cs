using System.Text;
using System.Text.Json;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class ProcessAgentToolResultJsonTests
{
    [Fact]
    public void ProjectionContainsOnlyBoundedProcessObservationFields()
    {
        var capturedAt = new DateTimeOffset(
            2026,
            7,
            25,
            9,
            30,
            0,
            TimeSpan.Zero);
        var startedAt = capturedAt.AddHours(-1);
        var process = new ProcessMonitorEntry(
            ProcessId: 0,
            Name: "asura",
            CpuPercent: 12.5,
            WorkingSetBytes: 8_192,
            TotalProcessorTime: TimeSpan.FromHours(4),
            StartedAtUtc: startedAt,
            IsAsura: true);
        var (result, intent, panelId) = Project(
            [process],
            capturedAt,
            enumerated: 5,
            observed: 1,
            truncated: true,
            limit: 16);

        var projection = ProcessAgentToolResultJson.Project(
            result,
            intent,
            panelId);

        Assert.True(projection.IsSuccess);
        Assert.Equal("processes_listed", projection.StableCode);
        Assert.True(
            Encoding.UTF8.GetByteCount(projection.Json)
            <= AgentProcessListResult.MaximumProjectionBytes);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            panelId.Value,
            root.GetProperty("panel_id").GetString());
        Assert.Equal(
            ProcessAgentToolResultJson.ContentOrigin,
            root.GetProperty("content_origin").GetString());
        Assert.Equal("cpu_desc", root.GetProperty("sort").GetString());
        Assert.Equal(16, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        Assert.Equal(1, root.GetProperty("returned").GetInt32());
        Assert.Equal(
            5,
            root.GetProperty("enumerated_process_count").GetInt32());
        Assert.Equal(
            1,
            root.GetProperty("observed_process_count").GetInt32());
        Assert.Equal(
            1,
            root.GetProperty("matching_process_count").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        var row = Assert.Single(
            root.GetProperty("processes").EnumerateArray());
        Assert.Equal(0, row.GetProperty("pid").GetInt32());
        Assert.Equal("asura", row.GetProperty("name").GetString());
        Assert.Equal(12.5, row.GetProperty("cpu_percent").GetDouble());
        Assert.Equal(
            8_192,
            row.GetProperty("working_set_bytes").GetInt64());
        Assert.Equal(
            startedAt,
            row.GetProperty("started_at_utc").GetDateTimeOffset());
        Assert.True(row.GetProperty("is_asura").GetBoolean());
        Assert.False(row.GetProperty("name_redacted").GetBoolean());
        Assert.False(row.GetProperty("name_truncated").GetBoolean());
        foreach (var forbidden in ForbiddenProcessFields)
        {
            Assert.DoesNotContain(
                forbidden,
                projection.Json,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SecretPathAndUnsafeNamesUseTheFixedRedaction()
    {
        var capturedAt = DateTimeOffset.UnixEpoch;
        ProcessMonitorEntry[] processes =
        [
            Entry(1, "api_key=secret-canary"),
            Entry(2, "/Users/private/bin/tool"),
            Entry(3, "unsafe\u0000name"),
        ];
        var (result, intent, panelId) = Project(
            processes,
            capturedAt,
            enumerated: 3,
            observed: 3,
            truncated: false,
            limit: 16);

        var projection = ProcessAgentToolResultJson.Project(
            result,
            intent,
            panelId);

        Assert.True(projection.IsSuccess);
        Assert.DoesNotContain(
            "secret-canary",
            projection.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "/Users/private",
            projection.Json,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(projection.Json);
        Assert.Equal(
            3,
            document.RootElement
                .GetProperty("redacted_name_count")
                .GetInt32());
        Assert.All(
            document.RootElement.GetProperty("processes").EnumerateArray(),
            row =>
            {
                Assert.Equal(
                    "[REDACTED PROCESS NAME]",
                    row.GetProperty("name").GetString());
                Assert.True(
                    row.GetProperty("name_redacted").GetBoolean());
            });
    }

    [Fact]
    public void MaximumRowsUseEscapedUtf8ByteBudgetAndRuneSafeNames()
    {
        var name = string.Concat(
            Enumerable.Repeat("界\"", 80));
        var processes = Enumerable.Range(1, 64)
            .Select(processId => Entry(processId, name))
            .ToArray();
        var (result, intent, panelId) = Project(
            processes,
            DateTimeOffset.UnixEpoch,
            enumerated: 64,
            observed: 64,
            truncated: false,
            limit: 64);

        var projection = ProcessAgentToolResultJson.Project(
            result,
            intent,
            panelId);

        Assert.True(projection.IsSuccess);
        var actualBytes = Encoding.UTF8.GetByteCount(projection.Json);
        Assert.True(
            actualBytes <= AgentProcessListResult.MaximumProjectionBytes,
            $"The escaped JSON used {actualBytes} bytes.");
        using var document = JsonDocument.Parse(projection.Json);
        var rows = document.RootElement
            .GetProperty("processes")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(64, rows.Length);
        Assert.All(
            rows,
            row =>
            {
                var projectedName =
                    row.GetProperty("name").GetString()!;
                Assert.True(
                    Encoding.UTF8.GetByteCount(projectedName)
                    <= AgentProcessDisplayName.MaximumTextBytes);
                Assert.True(
                    row.GetProperty("name_truncated").GetBoolean());
            });
    }

    [Fact]
    public void ResultCannotExceedTheProviderRequestedLimit()
    {
        var processes = Enumerable.Range(1, 32)
            .Select(processId => Entry(processId, $"process-{processId}"))
            .ToArray();
        var (result, _, panelId) = Project(
            processes,
            DateTimeOffset.UnixEpoch,
            enumerated: 32,
            observed: 32,
            truncated: false,
            limit: 32);

        var projection = ProcessAgentToolResultJson.Project(
            result,
            new ProcessAgentIntent(
                16,
                ProcessMonitorSort.CpuDescending),
            panelId);

        Assert.False(projection.IsSuccess);
        Assert.Equal(
            "processes_result_invalid",
            projection.StableCode);
        Assert.DoesNotContain(
            "process-1",
            projection.Json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MoreThanOneAsuraIdentityNeverReachesProviderProjection()
    {
        ProcessMonitorEntry[] processes =
        [
            Entry(1, "asura", isAsura: true),
            Entry(2, "asura-child", isAsura: true),
        ];
        Assert.Throws<ArgumentException>(() => Project(
            processes,
            DateTimeOffset.UnixEpoch,
            enumerated: 2,
            observed: 2,
            truncated: false,
            limit: 16));
    }

    [Theory]
    [InlineData(HostErrorCode.InvalidRequest, "target_changed")]
    [InlineData(HostErrorCode.NotFound, "target_changed")]
    [InlineData(HostErrorCode.RevisionConflict, "target_changed")]
    [InlineData(HostErrorCode.UnsupportedProtocol, "processes_unavailable")]
    [InlineData(HostErrorCode.CapabilityNotSupported, "processes_unavailable")]
    [InlineData(HostErrorCode.SessionClosed, "processes_unavailable")]
    [InlineData(HostErrorCode.DeadlineExceeded, "deadline_exceeded")]
    [InlineData(HostErrorCode.Cancelled, "cancelled")]
    [InlineData(HostErrorCode.EngineFailed, "processes_capture_failed")]
    public void HostFailuresMapToClosedSecretFreeCodes(
        HostErrorCode code,
        string expected)
    {
        var error = new HostError(
            code,
            "provider-secret-code",
            "password=secret-canary",
            Retryable: true);

        var stableCode =
            ProcessAgentToolResultJson.ProviderStableCode(error);
        var json = ProcessAgentToolResultJson.Failure(error);

        Assert.Equal(expected, stableCode);
        Assert.DoesNotContain(
            "provider-secret-code",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "secret-canary",
            json,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            expected,
            document.RootElement
                .GetProperty("error")
                .GetProperty("code")
                .GetString());
    }

    [Theory]
    [InlineData("authority_revoked")]
    [InlineData("caller_cancelled")]
    public void KnownCancellationCodesRemainDistinguishable(
        string stableCode)
    {
        var error = new HostError(
            HostErrorCode.Cancelled,
            stableCode,
            "Sensitive host cancellation details.");

        Assert.Equal(
            stableCode,
            ProcessAgentToolResultJson.ProviderStableCode(error));
    }

    private static (
        AgentProcessListResult Result,
        ProcessAgentIntent Intent,
        PanelInstanceId PanelId) Project(
            IReadOnlyList<ProcessMonitorEntry> processes,
            DateTimeOffset capturedAtUtc,
            int enumerated,
            int observed,
            bool truncated,
            int limit)
    {
        var context = ProcessContext();
        var panelId = context.Panels[0].PanelId;
        var intent = new ProcessAgentIntent(
            limit,
            ProcessMonitorSort.CpuDescending);
        var now = DateTimeOffset.UtcNow;
        var composer = new AgentProcessListActionComposer();
        var action = composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                new AgentRunId("process-json-run"),
                new ActorDescriptor(
                    new ActorId("process-json-agent"),
                    ActorKind.Agent,
                    "Process JSON agent"),
                policyGeneration: 1,
                createdAtUtc: now,
                deadlineUtc: now.AddMinutes(1)),
            context,
            new AgentProcessListRequest(
                panelId,
                limit,
                intent.Sort));
        var result = composer.Project(
            action,
            new ProcessMonitorSnapshot(
                capturedAtUtc,
                processes,
                enumerated,
                observed,
                truncated));
        return (result, intent, panelId);
    }

    private static ProcessMonitorEntry Entry(
        int processId,
        string name,
        bool isAsura = false) =>
        new(
            processId,
            name,
            CpuPercent: 1,
            WorkingSetBytes: 1_024,
            TotalProcessorTime: TimeSpan.FromSeconds(1),
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            IsAsura: isAsura);

    private static AgentContextSnapshot ProcessContext()
    {
        var windowId = new WindowInstanceId("process-json-window");
        var workspaceId = new WorkspaceInstanceId(
            "process-json-workspace");
        var tabId = new TabInstanceId("process-json-tab");
        var panelId = new PanelInstanceId("process-json-panel");
        var sessionId = new SessionId("process-json-session");
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(
                workspaceId,
                "Process JSON workspace",
                [
                    new TabInstance(
                        tabId,
                        "Process JSON tab",
                        [
                            new PanelInstance(
                                panelId,
                                PanelKind.ProcessMonitor,
                                "Process Monitor",
                                sessionId),
                        ],
                        panelId),
                ],
                tabId),
            revision: 1,
            lastSequence: 1);
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.ProcessMonitor,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            new CapabilitySet([SessionCapabilities.ProcessesList]),
            Revision: 1,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return new AgentContextSnapshot(
            new AgentTarget.Panel(
                windowId,
                workspaceId,
                tabId,
                panelId),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    tabId,
                    panelId,
                    descriptor),
            ],
            DateTimeOffset.UtcNow);
    }

    private static readonly string[] ForbiddenProcessFields =
    [
        "total_processor_time",
        "command_line",
        "executable_path",
        "environment",
        "username",
        "open_files",
    ];
}

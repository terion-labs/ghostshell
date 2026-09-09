using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentProcessListActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(AgentProcessListRequest.MinimumLimit)]
    [InlineData(AgentProcessListRequest.DefaultLimit)]
    [InlineData(AgentProcessListRequest.MaximumLimit)]
    public void Request_accepts_only_the_three_bounded_result_limits(int limit)
    {
        var request = new AgentProcessListRequest(
            ProcessPanel(),
            limit,
            ProcessMonitorSort.MemoryDescending);

        Assert.Equal(ProcessPanel(), request.PanelId);
        Assert.Equal(limit, request.Limit);
        Assert.Equal(ProcessMonitorSort.MemoryDescending, request.Sort);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(63)]
    [InlineData(65)]
    public void Request_rejects_non_enumerated_limits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentProcessListRequest(ProcessPanel(), limit));
    }

    [Fact]
    public void Request_rejects_invalid_panel_and_sort_values()
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentProcessListRequest(default));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentProcessListRequest(
                ProcessPanel(),
                sort: (ProcessMonitorSort)999));
    }

    [Theory]
    [InlineData(ProcessMonitorSort.CpuAscending)]
    [InlineData(ProcessMonitorSort.MemoryAscending)]
    [InlineData(ProcessMonitorSort.NameDescending)]
    [InlineData(ProcessMonitorSort.ProcessIdDescending)]
    public void Request_keeps_human_only_reverse_sorts_out_of_the_agent_contract(
        ProcessMonitorSort sort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentProcessListRequest(ProcessPanel(), sort: sort));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preparation_narrows_tab_and_workspace_scopes_to_one_exact_panel(
        bool workspaceScope)
    {
        var graph = Graph();
        AgentTarget target = workspaceScope
            ? new AgentTarget.Workspace(Window(), Workspace())
            : new AgentTarget.OpenTab(Window(), Workspace(), Tab());
        var context = BroadContext(graph, target);
        var request = new AgentProcessListRequest(
            ProcessPanel(),
            AgentProcessListRequest.MaximumLimit,
            ProcessMonitorSort.MemoryDescending);

        var action = new AgentProcessListActionComposer().Prepare(
            Envelope(),
            context,
            request);

        Assert.Same(request, action.Request);
        Assert.Equal(BuiltInAgentTools.ProcessesList, action.Proposal.ToolName);
        Assert.Equal(ExactProcessPanel(), action.Proposal.Target);
        Assert.Equal(
            AgentTargetIdentity.Create(ExactProcessPanel()),
            action.Proposal.TargetIdentity);
        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("panel_id", ProcessPanel().Value),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("sort", "memory_desc"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("limit", "64"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("offset", "0"),
                (argument.Name, argument.DisplayValue)));
    }

    [Fact]
    public void Exact_session_scope_is_preserved_and_fresh_binding_tracks_revision()
    {
        var composer = new AgentProcessListActionComposer();
        var target = new AgentTarget.ConnectionSession(ProcessSession());
        var action = composer.Prepare(
            Envelope(),
            ExactContext(
                target,
                graphRevision: 11,
                sessionRevision: 17),
            new AgentProcessListRequest(ProcessPanel()));

        var binding = composer.BindForExecution(
            action,
            ExactContext(
                target,
                graphRevision: 12,
                sessionRevision: 18));

        Assert.Equal(target, action.Proposal.Target);
        Assert.Equal(target, binding.Target);
        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            binding.TargetFingerprint);
        Assert.Equal(
            action.Proposal.ArgumentDigest,
            binding.ArgumentDigest);
    }

    [Fact]
    public void Digest_binds_panel_sort_limit_filters_and_action_identity()
    {
        var composer = new AgentProcessListActionComposer();
        var context = ExactContext(ExactProcessPanel());
        var envelope = Envelope();
        var baseline = composer.Prepare(
            envelope,
            context,
            new AgentProcessListRequest(
                ProcessPanel(),
                16,
                ProcessMonitorSort.CpuDescending));
        var changedSort = composer.Prepare(
            envelope,
            context,
            new AgentProcessListRequest(
                ProcessPanel(),
                16,
                ProcessMonitorSort.NameAscending));
        var changedLimit = composer.Prepare(
            envelope,
            context,
            new AgentProcessListRequest(
                ProcessPanel(),
                32,
                ProcessMonitorSort.CpuDescending));
        var changedFilters = composer.Prepare(
            envelope,
            context,
            new AgentProcessListRequest(
                ProcessPanel(),
                16,
                ProcessMonitorSort.CpuDescending,
                offset: 16,
                nameContains: "dotnet",
                processId: 42));

        Assert.NotEqual(
            baseline.Proposal.ArgumentDigest,
            changedSort.Proposal.ArgumentDigest);
        Assert.NotEqual(
            baseline.Proposal.ArgumentDigest,
            changedLimit.Proposal.ArgumentDigest);
        Assert.NotEqual(
            baseline.Proposal.ArgumentDigest,
            changedFilters.Proposal.ArgumentDigest);
    }

    [Fact]
    public void Projection_rejects_rows_outside_authorized_filters()
    {
        var composer = new AgentProcessListActionComposer();
        var context = ExactContext(ExactProcessPanel());
        var request = new AgentProcessListRequest(
            ProcessPanel(),
            16,
            ProcessMonitorSort.ProcessIdAscending,
            nameContains: "worker",
            processId: 42);
        var action = composer.Prepare(Envelope(), context, request);

        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            new ProcessMonitorSnapshot(
                Now,
                [Process(42, "other")],
                EnumeratedProcessCount: 2,
                ObservedProcessCount: 2,
                IsTruncated: false,
                MatchingProcessCount: 1)));
        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            new ProcessMonitorSnapshot(
                Now,
                [Process(7, "worker")],
                EnumeratedProcessCount: 2,
                ObservedProcessCount: 2,
                IsTruncated: false,
                MatchingProcessCount: 1)));
    }

    [Fact]
    public void Preparation_and_binding_fail_closed_on_stale_or_unsupported_context()
    {
        var composer = new AgentProcessListActionComposer();
        var request = new AgentProcessListRequest(ProcessPanel());

        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(
                ExactProcessPanel(),
                kind: PanelKind.Statistics),
            request));
        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(
                ExactProcessPanel(),
                lifecycle: SessionLifecycle.Starting),
            request));
        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(
                ExactProcessPanel(),
                includeCapability: false),
            request));
        Assert.Throws<ArgumentException>(() => composer.Prepare(
            Envelope(),
            ExactContext(
                new AgentTarget.SelectedPanels([ExactProcessPanel()])),
            request));

        var action = composer.Prepare(
            Envelope(),
            ExactContext(ExactProcessPanel()),
            request);
        Assert.Throws<ArgumentException>(() => composer.BindForExecution(
            action,
            BroadContext(
                Graph(),
                new AgentTarget.Workspace(Window(), Workspace()))));
        Assert.Throws<ArgumentException>(() => composer.BindForExecution(
            action,
            ExactContext(
                ExactProcessPanel(),
                includeCapability: false)));
    }

    [Fact]
    public void Projection_orders_safe_fields_and_excludes_sensitive_source_metadata()
    {
        var composer = new AgentProcessListActionComposer();
        var action = Action(
            composer,
            ProcessMonitorSort.MemoryDescending);
        var source = new List<ProcessMonitorEntry>
        {
            Process(
                9,
                "small",
                cpuPercent: 80,
                workingSetBytes: 100,
                totalProcessorTime: TimeSpan.FromHours(9)),
            Process(
                4,
                "large",
                cpuPercent: 2,
                workingSetBytes: 2_000,
                totalProcessorTime: TimeSpan.FromHours(4)),
        };

        var result = composer.Project(
            action,
            Snapshot(source, enumeratedCount: 7, observedCount: 2));
        source[0] = Process(99, "changed");

        Assert.Equal([4, 9], result.Processes.Select(item => item.ProcessId));
        Assert.Equal(7, result.EnumeratedProcessCount);
        Assert.Equal(2, result.ObservedProcessCount);
        Assert.Equal(2, result.ReturnedCount);
        Assert.Equal(2_000, result.Processes[0].WorkingSetBytes);
        Assert.Equal(2, result.Processes[0].ProcessorUsagePercent);
        Assert.Equal("small", result.Processes[1].Name.Text);
        Assert.DoesNotContain(
            typeof(AgentProcessListEntry).GetProperties(),
            property => property.Name.Contains(
                    "Command",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "Path",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "User",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "Environment",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "File",
                    StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains(
                    "TotalProcessor",
                    StringComparison.OrdinalIgnoreCase));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AgentProcessListEntry>)result.Processes)
            .Add(result.Processes[0]));
    }

    [Fact]
    public void Projection_redacts_malformed_unsafe_secret_and_path_names()
    {
        var composer = new AgentProcessListActionComposer();
        var action = Action(composer);
        var longName = string.Concat(
            Enumerable.Repeat("😀", 100));

        var result = composer.Project(
            action,
            Snapshot(
            [
                Process(1, "ordinary-process"),
                Process(2, "api_key=literal-secret-value"),
                Process(3, "/usr/local/bin/private-tool"),
                Process(4, "line\nbreak"),
                Process(5, "\uD800"),
                Process(6, "   "),
                Process(7, longName),
            ],
            enumeratedCount: 7,
            observedCount: 7));

        Assert.Equal("ordinary-process", result.Processes[0].Name.Text);
        Assert.Equal(5, result.RedactedNameCount);
        Assert.Equal(1, result.TruncatedNameCount);
        Assert.All(
            result.Processes.Where(process => process.Name.Redacted),
            process => Assert.Equal(
                "[REDACTED PROCESS NAME]",
                process.Name.Text));
        var truncated = Assert.Single(
            result.Processes,
            process => process.Name.Truncated);
        Assert.True(
            Encoding.UTF8.GetByteCount(truncated.Name.Text)
            <= AgentProcessDisplayName.MaximumTextBytes);
        _ = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true)
            .GetBytes(truncated.Name.Text);
        Assert.DoesNotContain(
            result.Processes,
            process => process.Name.Text.Contains(
                "literal-secret",
                StringComparison.Ordinal)
                || process.Name.Text.Contains(
                    "/usr/local",
                    StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1, 1, 0, false, false, false)]
    [InlineData(1, 1, -1, false, false, false)]
    [InlineData(1, 1, 0, true, false, false)]
    [InlineData(1, 1, 0, false, true, false)]
    [InlineData(1, 1, 0, false, false, true)]
    public void Projection_rejects_invalid_numeric_and_timestamp_metadata(
        int processId,
        long workingSetBytes,
        double cpuPercent,
        bool infiniteCpu,
        bool excessiveCpu,
        bool nonUtcStart)
    {
        var composer = new AgentProcessListActionComposer();
        var action = Action(composer);
        var cpu = infiniteCpu
            ? double.PositiveInfinity
            : excessiveCpu
                ? 101
                : cpuPercent;
        var started = nonUtcStart
            ? new DateTimeOffset(
                2026,
                7,
                25,
                12,
                0,
                0,
                TimeSpan.FromHours(2))
            : Now;

        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            Snapshot(
                [
                    Process(
                        processId,
                        "invalid",
                        cpu,
                        workingSetBytes,
                        startedAtUtc: started),
                ],
                enumeratedCount: 1,
                observedCount: 1)));
    }

    [Fact]
    public void Projection_rejects_inconsistent_counts_duplicates_and_multiple_self_rows()
    {
        var composer = new AgentProcessListActionComposer();
        var action = Action(composer);

        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            Snapshot(
                [Process(1, "one")],
                enumeratedCount: 0,
                observedCount: 0)));
        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            Snapshot(
                [Process(1, "one")],
                enumeratedCount: 1,
                observedCount: 2)));
        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            Snapshot(
                [Process(1, "one"), Process(1, "duplicate")],
                enumeratedCount: 2,
                observedCount: 2)));
        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            Snapshot(
                [
                    Process(1, "self-one", isAsura: true),
                    Process(2, "self-two", isAsura: true),
                ],
                enumeratedCount: 2,
                observedCount: 2)));
    }

    [Fact]
    public void Projection_rejects_over_limit_null_or_mutating_collections()
    {
        var composer = new AgentProcessListActionComposer();
        var action = Action(
            composer,
            limit: AgentProcessListRequest.MinimumLimit);
        var overLimit = Enumerable.Range(
                1,
                AgentProcessListRequest.MinimumLimit + 1)
            .Select(index => Process(index, $"process-{index}"))
            .ToArray();

        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            Snapshot(
                overLimit,
                enumeratedCount: overLimit.Length,
                observedCount: overLimit.Length)));
        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            Snapshot(
                [null!],
                enumeratedCount: 1,
                observedCount: 0)));
        Assert.Throws<ArgumentException>(() => composer.Project(
            action,
            Snapshot(
                new ChangingProcessList(Process(1, "one")),
                enumeratedCount: 1,
                observedCount: 1)));
    }

    [Fact]
    public void Maximum_projection_remains_within_the_fixed_json_envelope()
    {
        var composer = new AgentProcessListActionComposer();
        var action = Action(
            composer,
            limit: AgentProcessListRequest.MaximumLimit);
        var hostileButSafeName = new string('<', 128);
        var processes = Enumerable.Range(
                1,
                AgentProcessListRequest.MaximumLimit)
            .Select(index => Process(
                index,
                hostileButSafeName,
                cpuPercent: 100,
                workingSetBytes: long.MaxValue,
                startedAtUtc: Now))
            .ToArray();

        var result = composer.Project(
            action,
            Snapshot(
                processes,
                enumeratedCount: processes.Length,
                observedCount: processes.Length));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);

        Assert.Equal(
            AgentProcessListResult.MaximumEntries,
            result.ReturnedCount);
        Assert.True(
            bytes.Length <= AgentProcessListResult.MaximumProjectionBytes,
            $"Serialized process projection was {bytes.Length} bytes.");
    }

    [Fact]
    public void Contracts_expose_only_closed_immutable_process_shapes()
    {
        var hostMethod = Assert.Single(
            typeof(IAgentProcessSessionHost).GetMethods());

        Assert.Empty(typeof(AgentProcessListAction).GetConstructors());
        Assert.Empty(typeof(AgentProcessListResult).GetConstructors());
        Assert.Empty(typeof(AgentProcessListEntry).GetConstructors());
        Assert.Empty(typeof(AgentProcessDisplayName).GetConstructors());
        Assert.Equal("RunAgentProcessListAsync", hostMethod.Name);
        Assert.DoesNotContain(
            hostMethod.GetParameters(),
            parameter => parameter.ParameterType == typeof(object));
        Assert.All(
            typeof(AgentProcessListResult)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Null(property.SetMethod));
        Assert.All(
            typeof(AgentProcessListEntry)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => Assert.Null(property.SetMethod));

        Assert.True(BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.ProcessesList,
            out var descriptor));
        Assert.Equal(AgentCapability.ProcessData, descriptor!.Capability);
        Assert.Equal(AgentActionRisk.Observation, descriptor.Risk);
    }

    [Fact]
    public void Completion_result_count_is_optional_bounded_evidence()
    {
        var absent = new AgentActionCompletion(
            AgentActionOutcome.Succeeded,
            "ok",
            Now);
        var present = new AgentActionCompletion(
            AgentActionOutcome.Succeeded,
            "ok",
            Now,
            resultCount: 17);

        Assert.Null(absent.ResultCount);
        Assert.Equal(17, present.ResultCount);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentActionCompletion(
                AgentActionOutcome.Succeeded,
                "ok",
                Now,
                resultCount: -1));
    }

    private static AgentProcessListAction Action(
        AgentProcessListActionComposer composer,
        ProcessMonitorSort sort = ProcessMonitorSort.ProcessIdAscending,
        int limit = AgentProcessListRequest.DefaultLimit) =>
        composer.Prepare(
            Envelope(),
            ExactContext(ExactProcessPanel()),
            new AgentProcessListRequest(ProcessPanel(), limit, sort));

    private static ProcessMonitorSnapshot Snapshot(
        IReadOnlyList<ProcessMonitorEntry> processes,
        int enumeratedCount,
        int observedCount,
        bool truncated = false) =>
        new(
            Now,
            processes,
            enumeratedCount,
            observedCount,
            truncated);

    private static ProcessMonitorEntry Process(
        int processId,
        string name,
        double? cpuPercent = null,
        long? workingSetBytes = null,
        TimeSpan? totalProcessorTime = null,
        DateTimeOffset? startedAtUtc = null,
        bool isAsura = false) =>
        new(
            processId,
            name,
            cpuPercent,
            workingSetBytes,
            totalProcessorTime,
            startedAtUtc,
            isAsura);

    private static AgentContextSnapshot BroadContext(
        WorkspaceGraphSnapshot graph,
        AgentTarget target) =>
        new(
            target,
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    ProcessPanel(),
                    Descriptor()),
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    OtherPanel(),
                    session: null),
            ],
            Now);

    private static AgentContextSnapshot ExactContext(
        AgentTarget target,
        long graphRevision = 11,
        long sessionRevision = 17,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        PanelKind kind = PanelKind.ProcessMonitor,
        bool includeCapability = true)
    {
        var graph = Graph(graphRevision, kind);
        return new AgentContextSnapshot(
            target,
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    ProcessPanel(),
                    Descriptor(
                        sessionRevision,
                        lifecycle,
                        kind,
                        includeCapability)),
            ],
            Now);
    }

    private static WorkspaceGraphSnapshot Graph(
        long revision = 11,
        PanelKind processKind = PanelKind.ProcessMonitor)
    {
        var process = new PanelInstance(
            ProcessPanel(),
            processKind,
            "Processes",
            ProcessSession());
        var other = new PanelInstance(
            OtherPanel(),
            PanelKind.Statistics,
            "Statistics",
            sessionId: null);
        var tab = new TabInstance(
            Tab(),
            "Local",
            [process, other],
            process.Id);
        return new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(
                Workspace(),
                "Operations",
                [tab],
                tab.Id),
            revision,
            revision);
    }

    private static SessionDescriptor Descriptor(
        long revision = 17,
        SessionLifecycle lifecycle = SessionLifecycle.Active,
        PanelKind kind = PanelKind.ProcessMonitor,
        bool includeCapability = true) =>
        new(
            ProcessSession(),
            kind,
            lifecycle,
            lifecycle == SessionLifecycle.Active
                ? SessionHealth.Healthy
                : SessionHealth.Starting,
            new SessionOwner(
                HostMode.Desktop,
                Window(),
                Workspace(),
                Tab(),
                ProcessPanel()),
            includeCapability
                ? new CapabilitySet([SessionCapabilities.ProcessesList])
                : CapabilitySet.Empty,
            revision,
            HasActiveWork: false,
            StatusDetail: "Ready");

    private static AgentActionEnvelope Envelope() =>
        new(
            new AgentActionId("process-action"),
            new AgentRunId("process-run"),
            new ActorDescriptor(
                new ActorId("process-agent"),
                ActorKind.Agent,
                "Process agent"),
            policyGeneration: 3,
            Now,
            Now.AddMinutes(1));

    private static AgentTarget.Panel ExactProcessPanel() =>
        new(Window(), Workspace(), Tab(), ProcessPanel());

    private static WindowInstanceId Window() => new("process-window");

    private static WorkspaceInstanceId Workspace() =>
        new("process-workspace");

    private static TabInstanceId Tab() => new("process-tab");

    private static PanelInstanceId ProcessPanel() => new("process-panel");

    private static PanelInstanceId OtherPanel() => new("statistics-panel");

    private static SessionId ProcessSession() => new("process-session");

    private sealed class ChangingProcessList(ProcessMonitorEntry process)
        : IReadOnlyList<ProcessMonitorEntry>
    {
        private bool _read;

        public int Count => _read ? 0 : 1;

        public ProcessMonitorEntry this[int index]
        {
            get
            {
                if (index != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                _read = true;
                return process;
            }
        }

        public IEnumerator<ProcessMonitorEntry> GetEnumerator()
        {
            yield return process;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

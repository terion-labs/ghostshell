using System.Text;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentDatabaseReadActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> ToolNames()
    {
        yield return [BuiltInAgentTools.DatabaseReadState];
        yield return [BuiltInAgentTools.DatabaseListObjects];
        yield return [BuiltInAgentTools.DatabaseDescribeObject];
        yield return [BuiltInAgentTools.DatabaseReadTable];
        yield return [BuiltInAgentTools.DatabaseSchemaGraph];
        yield return [BuiltInAgentTools.RedisScan];
        yield return [BuiltInAgentTools.RedisRead];
        yield return [BuiltInAgentTools.RedisListIndexes];
        yield return [BuiltInAgentTools.RedisSearch];
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void CatalogUsesDedicatedReadOnlyCapability(string toolName)
    {
        Assert.True(BuiltInAgentTools.Catalog.TryGet(toolName, out var descriptor));
        Assert.Equal(AgentCapability.DatabaseRead, descriptor!.Capability);
        Assert.Equal(AgentActionRisk.Observation, descriptor.Risk);
        Assert.Equal(
            AgentPermission.Off,
            AgentPolicy.Default.GetPermission(AgentCapability.DatabaseRead));
    }

    [Fact]
    public void PreparationNarrowsBroadScopeAndDoesNotPresentSecretArguments()
    {
        const string privateArgument = "not-for-presentation";
        var composer = new AgentDatabaseReadActionComposer();
        var request = new AgentDatabaseReadRequest.ReadTable(
            DatabasePanel(),
            new DatabaseObjectReference("object_ref_1"),
            [new AgentDatabaseFilter(
                "password",
                DatabaseFilterOperator.Equal,
                new AgentDatabaseFilterValue.Text(privateArgument))],
            [],
            offset: 0,
            limit: 20);

        var action = composer.Prepare(
            Envelope(),
            Context(new AgentTarget.Workspace(Window(), Workspace())),
            request);

        Assert.Equal(ExactPanel(), action.Proposal.Target);
        Assert.Equal(BuiltInAgentTools.DatabaseReadTable, action.Proposal.ToolName);
        Assert.DoesNotContain(
            action.Proposal.Presentation.Arguments,
            argument => argument.DisplayValue.Contains(
                privateArgument,
                StringComparison.Ordinal));
        Assert.Contains(
            action.Proposal.Presentation.Arguments,
            argument => string.Equals(argument.Name, "filter_count", StringComparison.Ordinal) && string.Equals(argument.DisplayValue, "1", StringComparison.Ordinal));
    }

    [Fact]
    public void BindingRequiresFreshLiveCapabilityAndPreservesTypedDigest()
    {
        var composer = new AgentDatabaseReadActionComposer();
        var request = new AgentDatabaseReadRequest.RedisSearch(
            DatabasePanel(),
            "users-index",
            "@name:{not-for-presentation}",
            10);
        var action = composer.Prepare(
            Envelope(),
            ExactContext(
                SessionCapabilities.RedisSearch,
                graphRevision: 11,
                sessionRevision: 17),
            request);

        var binding = composer.BindForExecution(
            action,
            ExactContext(
                SessionCapabilities.RedisSearch,
                graphRevision: 12,
                sessionRevision: 18));

        Assert.NotEqual(action.Proposal.TargetFingerprint, binding.TargetFingerprint);
        Assert.Equal(action.Proposal.ArgumentDigest, binding.ArgumentDigest);
        Assert.DoesNotContain(
            action.Proposal.Presentation.Arguments,
            argument => argument.DisplayValue.Contains(
                "not-for-presentation",
                StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => composer.BindForExecution(
            action,
            ExactContext(SessionCapabilities.RedisRead)));
    }

    [Fact]
    public void ProjectionUsesStrictUtf8AndRuneSafeCellBudget()
    {
        var composer = new AgentDatabaseReadActionComposer();
        var reference = new DatabaseObjectReference("object_ref_1");
        var action = composer.Prepare(
            Envelope(),
            ExactContext(SessionCapabilities.DatabaseReadTable),
            new AgentDatabaseReadRequest.ReadTable(
                DatabasePanel(),
                reference,
                [],
                [],
                offset: 0,
                limit: 1,
                columns: ["value"],
                maximumCellBytes: 256));
        var source = string.Concat(Enumerable.Repeat("🙂", 3_000));
        var snapshot = new DatabaseTableSnapshot(
            Object(reference),
            new DatabaseTablePage(
                new DatabaseQueryPage(
                    [new DatabaseColumnDescriptor("value", "text")],
                    [[source]],
                    Truncated: false,
                    RowsAffected: 0,
                    TimeSpan.Zero),
                Offset: 0,
                Limit: 1,
                HasMore: false,
                TotalRows: 1));

        var result = Assert.IsType<AgentDatabaseReadResult.Table>(
            composer.Project(action, snapshot));
        var cell = Assert.Single(Assert.Single(result.Value.Page.Result.Rows));

        Assert.NotNull(cell);
        Assert.True(Encoding.UTF8.GetByteCount(cell) <= 256);
        Assert.DoesNotContain('\uFFFD', cell);
        Assert.True(result.Value.Page.Result.Truncated);
        Assert.True(result.Value.Page.HasMore);
        Assert.Null(result.Value.Page.Result.TypedRows);
    }

    [Fact]
    public void TableProjectionRejectsColumnsOutsideTheAuthorizedProjection()
    {
        var composer = new AgentDatabaseReadActionComposer();
        var reference = new DatabaseObjectReference("object_ref_1");
        var action = composer.Prepare(
            Envelope(),
            ExactContext(SessionCapabilities.DatabaseReadTable),
            new AgentDatabaseReadRequest.ReadTable(
                DatabasePanel(),
                reference,
                [],
                [],
                offset: 0,
                limit: 1,
                columns: ["id"]));
        var snapshot = new DatabaseTableSnapshot(
            Object(reference),
            new DatabaseTablePage(
                new DatabaseQueryPage(
                    [new DatabaseColumnDescriptor("secret", "text")],
                    [["value"]],
                    Truncated: false,
                    RowsAffected: 0,
                    TimeSpan.Zero),
                Offset: 0,
                Limit: 1,
                HasMore: false,
                TotalRows: 1,
                TableRows: 3));

        Assert.Throws<ArgumentException>(() => composer.Project(action, snapshot));
    }

    [Fact]
    public void ProjectionRejectsInvalidUnicodeAndOversizedMetadataAggregate()
    {
        var composer = new AgentDatabaseReadActionComposer();
        var action = composer.Prepare(
            Envelope(),
            ExactContext(SessionCapabilities.DatabaseDescribeObject),
            new AgentDatabaseReadRequest.DescribeObject(
                DatabasePanel(),
                new DatabaseObjectReference("object_ref_1")));
        var invalidUnicode = new string(['b', 'a', 'd', '\uD800']);
        var invalidSnapshot = new DatabaseObjectSnapshot(
            Object(new DatabaseObjectReference("object_ref_1")),
            [new DatabaseColumnSchema(
                invalidUnicode,
                0,
                "text",
                DatabaseValueKind.Text)],
            [],
            CanEdit: false,
            ReadOnlyReason: null);

        Assert.Throws<ArgumentException>(() => composer.Project(action, invalidSnapshot));

        var columns = Enumerable.Range(0, 200)
            .Select(index => new DatabaseColumnSchema(
                $"column_{index}",
                index,
                "text",
                DatabaseValueKind.Text,
                DefaultExpression: new string('x', 1_000)))
            .ToArray();
        var oversized = invalidSnapshot with { Columns = columns };

        Assert.Throws<ArgumentException>(() => composer.Project(action, oversized));
    }

    [Theory]
    [InlineData("password=hunter2")]
    [InlineData("authorization: bearer abcdefghijklmnop")]
    [InlineData("ghp_0123456789abcdef")]
    public void ModelVisibleDatabaseArgumentsRejectLiteralSecrets(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            new AgentDatabaseFilterValue.Text(value));
        Assert.Throws<ArgumentException>(() =>
            new AgentDatabaseReadRequest.RedisSearch(
                DatabasePanel(),
                "users-index",
                value,
                10));
        Assert.Throws<ArgumentException>(() =>
            new AgentDatabaseReadRequest.RedisScan(
                DatabasePanel(),
                value,
                cursor: null,
                count: 10));
    }

    private static DatabaseObjectSummary Object(DatabaseObjectReference reference) =>
        new(reference, "widgets", DatabaseTableKind.Table, "catalog", "public");

    private static AgentContextSnapshot Context(AgentTarget target) =>
        new(
            target,
            [AgentContextPanel.ForGraphPanel(
                Graph(),
                Tab(),
                DatabasePanel(),
                Descriptor(AllCapabilities()))],
            Now);

    private static AgentContextSnapshot ExactContext(
        string capability,
        long graphRevision = 11,
        long sessionRevision = 17) =>
        new(
            ExactPanel(),
            [AgentContextPanel.ForGraphPanel(
                Graph(graphRevision),
                Tab(),
                DatabasePanel(),
                Descriptor([capability], sessionRevision))],
            Now);

    private static WorkspaceGraphSnapshot Graph(long revision = 11)
    {
        var panel = new PanelInstance(
            DatabasePanel(),
            PanelKind.DatabaseViewer,
            "Database",
            DatabaseSession());
        var tab = new TabInstance(Tab(), "Data", [panel], panel.Id);
        return new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(Workspace(), "Operations", [tab], tab.Id),
            revision,
            revision);
    }

    private static SessionDescriptor Descriptor(
        IReadOnlyList<string> capabilities,
        long revision = 17) =>
        new(
            DatabaseSession(),
            PanelKind.DatabaseViewer,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                Window(),
                Workspace(),
                Tab(),
                DatabasePanel()),
            new CapabilitySet(capabilities),
            revision,
            HasActiveWork: false,
            StatusDetail: "Ready");

    private static string[] AllCapabilities() =>
    [
        SessionCapabilities.DatabaseReadState,
        SessionCapabilities.DatabaseListObjects,
        SessionCapabilities.DatabaseDescribeObject,
        SessionCapabilities.DatabaseReadTable,
        SessionCapabilities.DatabaseSchemaGraph,
        SessionCapabilities.RedisScan,
        SessionCapabilities.RedisRead,
        SessionCapabilities.RedisListIndexes,
        SessionCapabilities.RedisSearch,
    ];

    private static AgentActionEnvelope Envelope() =>
        new(
            new AgentActionId($"database-action-{Guid.NewGuid():N}"),
            new AgentRunId("database-run"),
            new ActorDescriptor(
                new ActorId("database-agent"),
                ActorKind.Agent,
                "Database agent"),
            policyGeneration: 3,
            Now,
            Now.AddMinutes(1));

    private static AgentTarget.Panel ExactPanel() =>
        new(Window(), Workspace(), Tab(), DatabasePanel());

    private static WindowInstanceId Window() => new("database-window");

    private static WorkspaceInstanceId Workspace() => new("database-workspace");

    private static TabInstanceId Tab() => new("database-tab");

    private static PanelInstanceId DatabasePanel() => new("database-panel");

    private static SessionId DatabaseSession() => new("database-session");
}

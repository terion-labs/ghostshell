using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class DatabaseAgentToolContractTests
{
    [Fact]
    public void ExactRelationalSchemasAreClosedAndExposeOnlyLiveCapabilities()
    {
        var panel = ContextPanel(
            "relational",
            SessionCapabilities.DatabaseReadState,
            SessionCapabilities.DatabaseListObjects,
            SessionCapabilities.DatabaseDescribeObject,
            SessionCapabilities.DatabaseReadTable,
            SessionCapabilities.DatabaseSchemaGraph);

        var tools = DatabaseAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.DatabaseReadState,
                BuiltInAgentTools.DatabaseListObjects,
                BuiltInAgentTools.DatabaseDescribeObject,
                BuiltInAgentTools.DatabaseReadTable,
                BuiltInAgentTools.DatabaseSchemaGraph,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.All(tools, tool =>
        {
            Assert.False(tool.InputSchema
                .GetProperty("additionalProperties")
                .GetBoolean());
            Assert.DoesNotContain(
                "panel_id",
                tool.InputSchema.GetRawText(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "sql",
                tool.InputSchema.GetRawText(),
                StringComparison.OrdinalIgnoreCase);
        });

        var table = tools.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.DatabaseReadTable, StringComparison.Ordinal));
        var properties = table.InputSchema.GetProperty("properties");
        Assert.Equal(
            [
                "object_ref",
                "offset",
                "limit",
                "columns",
                "exclude_columns",
                "maximum_cell_bytes",
                "filters",
                "sorts",
            ],
            properties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.False(properties.GetProperty("filters")
            .GetProperty("items")
            .GetProperty("additionalProperties")
            .GetBoolean());
    }

    [Fact]
    public void RedisSearchIsAdvertisedOnlyFromTheLiveSessionCapability()
    {
        var withoutSearch = ContextPanel(
            "redis-basic",
            SessionCapabilities.DatabaseReadState,
            SessionCapabilities.RedisScan,
            SessionCapabilities.RedisRead);
        var withSearch = ContextPanel(
            "redis-search",
            SessionCapabilities.DatabaseReadState,
            SessionCapabilities.RedisScan,
            SessionCapabilities.RedisRead,
            SessionCapabilities.RedisListIndexes,
            SessionCapabilities.RedisSearch);

        Assert.Equal(
            [
                BuiltInAgentTools.DatabaseReadState,
                BuiltInAgentTools.RedisScan,
                BuiltInAgentTools.RedisRead,
            ],
            DatabaseAgentToolSet.For(withoutSearch).Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.Contains(
            DatabaseAgentToolSet.For(withSearch),
            tool => string.Equals(tool.Name, BuiltInAgentTools.RedisSearch, StringComparison.Ordinal));
        var listIndexes = Assert.Single(
            DatabaseAgentToolSet.For(withSearch),
            tool => string.Equals(tool.Name, BuiltInAgentTools.RedisListIndexes, StringComparison.Ordinal));
        Assert.True(listIndexes.InputSchema
            .GetProperty("properties")
            .TryGetProperty("maximum_indexes", out _));

        var broad = DatabaseAgentToolSet.For([withoutSearch, withSearch]);
        Assert.Equal(
            [withSearch.PanelId.Value],
            broad.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.RedisSearch, StringComparison.Ordinal))
                .InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            [withSearch.PanelId.Value],
            broad.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.RedisListIndexes, StringComparison.Ordinal))
                .InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ParserBuildsStructuredTableReadAndRejectsSqlOrUnknownFields()
    {
        var panel = ContextPanel(
            "table",
            SessionCapabilities.DatabaseReadTable);
        var parsed = Assert.IsType<DatabaseAgentIntentResult.Parsed>(
            DatabaseAgentToolParser.Parse(
                await ProposalAsync(
                    BuiltInAgentTools.DatabaseReadTable,
                    """
                    {
                      "object_ref": "object_ref_1",
                      "offset": 10,
                      "limit": 20,
                      "filters": [
                        {"column":"active","operator":"equal","value":true},
                        {"column":"id","operator":"in","value":[1,2,3]}
                      ],
                      "sorts": [{"column":"id","direction":"desc"}],
                      "columns": ["id", "active"],
                      "maximum_cell_bytes": 512
                    }
                    """),
                panel));
        var request = Assert.IsType<AgentDatabaseReadRequest.ReadTable>(parsed.Request);

        Assert.Equal(10, request.Offset);
        Assert.Equal(20, request.Limit);
        Assert.Equal(2, request.Filters.Count);
        Assert.True(Assert.IsType<AgentDatabaseFilterValue.Boolean>(
            request.Filters[0].Value).Value);
        Assert.Equal(3, Assert.IsType<AgentDatabaseFilterValue.List>(
            request.Filters[1].Value).Values.Count);
        Assert.True(Assert.Single(request.Sorts).Descending);
        Assert.Equal(["id", "active"], request.Columns);
        Assert.Empty(request.ExcludeColumns);
        Assert.Equal(512, request.MaximumCellBytes);

        Assert.IsType<DatabaseAgentIntentResult.Rejected>(
            DatabaseAgentToolParser.Parse(
                await ProposalAsync(
                    BuiltInAgentTools.DatabaseReadTable,
                    """{"object_ref":"object_ref_1","sql":"select * from secrets"}"""),
                panel));
        var duplicate = await RunProviderAsync(
            BuiltInAgentTools.DatabaseReadTable,
            """{"object_ref":"object_ref_1","limit":20,"limit":30}""");
        Assert.False(duplicate.Succeeded);
        Assert.Equal(AgentTurnErrorCode.InvalidProviderStream, duplicate.ErrorCode);
    }

    [Fact]
    public async Task ParserBuildsBoundedRedisIndexDiscovery()
    {
        var panel = ContextPanel(
            "redis-indexes",
            SessionCapabilities.RedisListIndexes);

        var parsed = Assert.IsType<DatabaseAgentIntentResult.Parsed>(
            DatabaseAgentToolParser.Parse(
                await ProposalAsync(
                    BuiltInAgentTools.RedisListIndexes,
                    """{"maximum_indexes":25}"""),
                panel));
        var request = Assert.IsType<AgentDatabaseReadRequest.RedisListIndexes>(
            parsed.Request);

        Assert.Equal(25, request.MaximumIndexes);
    }

    [Fact]
    public void ResultJsonRedactsSecretColumnsKeysFieldsAndContentPatterns()
    {
        var composer = new AgentDatabaseReadActionComposer();
        var tablePanel = ContextPanel(
            "table-result",
            SessionCapabilities.DatabaseReadTable);
        var tableRequest = new AgentDatabaseReadRequest.ReadTable(
            tablePanel.PanelId,
            new DatabaseObjectReference("object_ref_1"),
            [],
            [],
            offset: 0,
            limit: 1);
        var tableAction = Prepare(composer, tablePanel, tableRequest);
        var table = composer.Project(tableAction, new DatabaseTableSnapshot(
            new DatabaseObjectSummary(
                new DatabaseObjectReference("object_ref_1"),
                "users",
                DatabaseTableKind.Table),
            new DatabaseTablePage(
                new DatabaseQueryPage(
                    [
                        new DatabaseColumnDescriptor("password", "text"),
                        new DatabaseColumnDescriptor("notes", "text"),
                    ],
                    [["hunter2", "ghp_0123456789abcdef0123456789abcdef"]],
                    Truncated: false,
                    RowsAffected: 0,
                    TimeSpan.Zero),
                Offset: 0,
                Limit: 1,
                HasMore: false,
                TotalRows: 1,
                TableRows: 7)));

        var tableProjection = DatabaseAgentToolResultJson.Project(table);
        Assert.True(tableProjection.IsSuccess);
        Assert.DoesNotContain("hunter2", tableProjection.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", tableProjection.Json, StringComparison.OrdinalIgnoreCase);
        using (var document = JsonDocument.Parse(tableProjection.Json))
        {
            Assert.True(document.RootElement.GetProperty("redaction_count").GetInt32() >= 2);
            Assert.Equal(
                DatabaseAgentToolResultJson.ContentOrigin,
                document.RootElement.GetProperty("content_origin").GetString());
            Assert.Equal(1, document.RootElement
                .GetProperty("filtered_row_count")
                .GetInt64());
            Assert.Equal(7, document.RootElement
                .GetProperty("table_row_count")
                .GetInt64());
            Assert.False(document.RootElement.TryGetProperty("total_rows", out _));
        }

        var redisPanel = ContextPanel(
            "redis-result",
            SessionCapabilities.RedisRead);
        var redisRequest = new AgentDatabaseReadRequest.RedisRead(
            redisPanel.PanelId,
            new RedisKeyReferenceId("redis_ref_1"),
            maximumEntries: 1);
        var redisAction = Prepare(composer, redisPanel, redisRequest);
        var redis = composer.Project(
            redisAction,
            new RedisKeyValueSnapshot(
                new RedisKeyItem(
                    new RedisKeyReferenceId("redis_ref_1"),
                    "authorization:cookie",
                    "hash",
                    TimeToLive: null,
                    MemoryBytes: 32),
                Length: 1,
                [new RedisValueEntry("password", "api_key", "redis-secret")],
                IsTruncated: false,
                Limitation: null));
        var redisProjection = DatabaseAgentToolResultJson.Project(redis);

        Assert.True(redisProjection.IsSuccess);
        Assert.DoesNotContain("redis-secret", redisProjection.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("authorization:cookie", redisProjection.Json, StringComparison.Ordinal);
        using var redisDocument = JsonDocument.Parse(redisProjection.Json);
        Assert.True(redisDocument.RootElement
            .GetProperty("redaction_count")
            .GetInt32() >= 4);
        Assert.True(
            Encoding.UTF8.GetByteCount(redisProjection.Json)
            <= AgentKernelLimits.Default.MaximumToolResultBytes);

        var indexPanel = ContextPanel(
            "redis-index-result",
            SessionCapabilities.RedisListIndexes);
        var indexAction = Prepare(
            composer,
            indexPanel,
            new AgentDatabaseReadRequest.RedisListIndexes(
                indexPanel.PanelId,
                maximumIndexes: 2));
        var indexResult = composer.Project(
            indexAction,
            new RedisSearchIndexPage(
                [new RedisSearchIndex(
                    "users",
                    "ON HASH PREFIX 1 user:",
                    "name TEXT",
                    12)],
                IsTruncated: false));
        var indexProjection = DatabaseAgentToolResultJson.Project(indexResult);
        using var indexDocument = JsonDocument.Parse(indexProjection.Json);

        Assert.True(indexProjection.IsSuccess);
        Assert.Equal("redis_indexes_listed", indexProjection.StableCode);
        Assert.Equal("users", indexDocument.RootElement
            .GetProperty("indexes")[0]
            .GetProperty("name")
            .GetString());
    }

    private static AgentDatabaseReadAction Prepare(
        AgentDatabaseReadActionComposer composer,
        AgentContextPanel panel,
        AgentDatabaseReadRequest request)
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var context = new AgentContextSnapshot(
            new AgentTarget.Panel(
                panel.WindowId,
                panel.WorkspaceId,
                panel.TabId,
                panel.PanelId),
            [panel],
            now);
        return composer.Prepare(
            new AgentActionEnvelope(
                AgentActionId.New(),
                new AgentRunId("database-result-run"),
                new ActorDescriptor(
                    new ActorId("database-result-agent"),
                    ActorKind.Agent,
                    "Database result agent"),
                policyGeneration: 1,
                now,
                now.AddMinutes(1)),
            context,
            request);
    }

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var result = await RunProviderAsync(name, arguments);
        Assert.True(result.Succeeded);
        return Assert.Single(result.ToolProposals);
    }

    private static async Task<AgentTurnResult> RunProviderAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(new AgentRunId("database-contract"));
        return await session.RunTurnAsync(
            "Use the database tool.",
            [Tool(name)],
            new ToolProvider(name, arguments),
            CancellationToken.None);
    }

    private static AgentToolDefinition Tool(string name) =>
        new(
            name,
            "Test database tool.",
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """u8.ToArray());

    private static AgentContextPanel ContextPanel(
        string suffix,
        params string[] capabilities)
    {
        var sessionId = new SessionId($"session-{suffix}");
        var windowId = new WindowInstanceId($"window-{suffix}");
        var workspaceId = new WorkspaceInstanceId($"workspace-{suffix}");
        var tabId = new TabInstanceId($"tab-{suffix}");
        var panelId = new PanelInstanceId($"panel-{suffix}");
        var panel = new PanelInstance(
            panelId,
            PanelKind.DatabaseViewer,
            $"Database {suffix}",
            sessionId);
        var tab = new TabInstance(tabId, "Data", [panel], panelId);
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(
                workspaceId,
                "Operations",
                [tab],
                tabId),
            revision: 2,
            lastSequence: 2);
        var descriptor = new SessionDescriptor(
            sessionId,
            PanelKind.DatabaseViewer,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            new CapabilitySet(capabilities),
            Revision: 4,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return AgentContextPanel.ForGraphPanel(
            graph,
            tabId,
            panelId,
            descriptor);
    }

    private sealed class ToolProvider(
        string name,
        string arguments) : IAgentProvider
    {
        public async IAsyncEnumerable<AgentProviderEvent> StreamAsync(
            AgentProviderRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AgentProviderEvent.ResponseStarted();
            yield return new AgentProviderEvent.ToolCallStarted(
                0,
                "database-call-1",
                ProviderToolName.FromInternal(name));
            yield return new AgentProviderEvent.ToolCallArgumentsDelta(0, arguments);
            yield return new AgentProviderEvent.ToolCallCompleted(0);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse);
            await Task.CompletedTask;
        }
    }
}

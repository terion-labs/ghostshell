using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class FileAgentToolContractTests
{
    [Fact]
    public void ExactSchemasExposeOnlySupportedRelativePathOperations()
    {
        var panel = ContextPanel(
            "exact",
            FilePanelCapability.List
            | FilePanelCapability.Stat
            | FilePanelCapability.RangedRead
            | FilePanelCapability.CreateDirectory
            | FilePanelCapability.Delete
            | FilePanelCapability.GovernedCreateDirectory
            | FilePanelCapability.GovernedDelete,
            SessionCapabilities.FilesList,
            SessionCapabilities.FilesStat,
            SessionCapabilities.FilesPreview,
            SessionCapabilities.FilesCreateDirectory,
            SessionCapabilities.FilesDelete);
        var metadata = panel.FileMetadata!;

        var tools = FileAgentToolSet.For(panel, metadata);

        Assert.Equal(
            [
                BuiltInAgentTools.FilesList,
                BuiltInAgentTools.FilesStat,
                BuiltInAgentTools.FilesRead,
                BuiltInAgentTools.FilesCreateDirectory,
                BuiltInAgentTools.FilesDelete,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.All(
            tools,
            tool =>
            {
                var schema = tool.InputSchema;
                Assert.False(
                    schema.GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(
string.Equals(tool.Name, BuiltInAgentTools.FilesDelete
, StringComparison.Ordinal) ? ["path_segments", "recursive", "entry_ref"]
                        : ["path_segments"],
                    schema.GetProperty("properties")
                        .EnumerateObject()
                        .Select(property => property.Name), StringComparer.Ordinal);
                Assert.Equal(
                    string.Equals(tool.Name, BuiltInAgentTools.FilesDelete, StringComparison.Ordinal)
                        ? ["path_segments", "entry_ref"]
                        : ["path_segments"],
                    schema.GetProperty("required")
                        .EnumerateArray()
                        .Select(value => value.GetString()), StringComparer.Ordinal);
                Assert.DoesNotContain(
                    "panel_id",
                    schema.GetRawText(),
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "session",
                    schema.GetRawText(),
                    StringComparison.OrdinalIgnoreCase);
                foreach (var forbidden in ForbiddenProviderFields)
                {
                    Assert.DoesNotContain(
                        forbidden,
                        schema.GetRawText(),
                        StringComparison.OrdinalIgnoreCase);
                }
            });

        Assert.Equal(
            0,
            tools.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.FilesList, StringComparison.Ordinal))
                .InputSchema.GetProperty("properties")
                .GetProperty("path_segments")
                .GetProperty("minItems")
                .GetInt32());
        Assert.Equal(
            0,
            tools.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.FilesStat, StringComparison.Ordinal))
                .InputSchema.GetProperty("properties")
                .GetProperty("path_segments")
                .GetProperty("minItems")
                .GetInt32());
        Assert.Equal(
            1,
            tools.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.FilesRead, StringComparison.Ordinal))
                .InputSchema.GetProperty("properties")
                .GetProperty("path_segments")
                .GetProperty("minItems")
                .GetInt32());
        Assert.Equal(
            1,
            tools.Single(
                    tool => string.Equals(tool.Name
, BuiltInAgentTools.FilesCreateDirectory, StringComparison.Ordinal))
                .InputSchema.GetProperty("properties")
                .GetProperty("path_segments")
                .GetProperty("minItems")
                .GetInt32());
        Assert.Equal(
            1,
            tools.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.FilesDelete, StringComparison.Ordinal))
                .InputSchema.GetProperty("properties")
                .GetProperty("path_segments")
                .GetProperty("minItems")
                .GetInt32());
    }

    [Fact]
    public void BroadSchemasAlwaysEnumerateOnlyEligibleFilePanels()
    {
        var list = ContextPanel(
            "list",
            FilePanelCapability.List,
            SessionCapabilities.FilesList);
        var read = ContextPanel(
            "read",
            FilePanelCapability.RangedRead,
            SessionCapabilities.FilesPreview);
        var mutations = ContextPanel(
            "mutations",
            FilePanelCapability.CreateDirectory
            | FilePanelCapability.Delete
            | FilePanelCapability.GovernedCreateDirectory
            | FilePanelCapability.GovernedDelete,
            SessionCapabilities.FilesCreateDirectory,
            SessionCapabilities.FilesDelete);
        var ordinaryMutations = ContextPanel(
            "ordinary-mutations",
            FilePanelCapability.CreateDirectory
            | FilePanelCapability.Delete,
            SessionCapabilities.FilesCreateDirectory,
            SessionCapabilities.FilesDelete);
        AgentContextPanel[] panels = [list, read, mutations, ordinaryMutations];
        var metadata = panels.ToDictionary(
            panel => panel.PanelId,
            panel => panel.FileMetadata!);

        var tools = FileAgentToolSet.For(panels, metadata);

        Assert.Equal(
            [list.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.FilesList));
        Assert.Equal(
            [read.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.FilesRead));
        Assert.Equal(
            [mutations.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.FilesCreateDirectory));
        Assert.Equal(
            [mutations.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.FilesDelete));
        Assert.DoesNotContain(
            tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.FilesStat, StringComparison.Ordinal));
        Assert.All(
            tools,
            tool => Assert.Contains(
                "panel_id",
                tool.InputSchema.GetProperty("required")
                    .EnumerateArray()
                    .Select(value => value.GetString()), StringComparer.Ordinal));

        var onePanelTools = FileAgentToolSet.For(
            [list],
            new Dictionary<PanelInstanceId, FileSessionMetadata>
            {
                [list.PanelId] = list.FileMetadata!,
            });
        Assert.Equal(
            [list.PanelId.Value],
            PanelIds(onePanelTools, BuiltInAgentTools.FilesList));
    }

    [Fact]
    public void WorkspaceSchemasAdvertiseFileMutationsOnlyForLiveCapablePanels()
    {
        var ordinary = ContextPanel(
            "workspace-ordinary",
            FilePanelCapability.Stat
            | FilePanelCapability.StreamingWrite
            | FilePanelCapability.Copy,
            SessionCapabilities.FilesStat,
            GovernedFileToolNames.SessionWrite,
            GovernedFileToolNames.SessionCopy);
        var governed = ContextPanel(
            "workspace-governed",
            FilePanelCapability.Stat
            | FilePanelCapability.StreamingWrite
            | FilePanelCapability.Copy
            | FilePanelCapability.GovernedCreateFile
            | FilePanelCapability.GovernedReplaceFile
            | FilePanelCapability.GovernedCopySource
            | FilePanelCapability.GovernedCopy,
            SessionCapabilities.FilesStat,
            GovernedFileToolNames.SessionWrite,
            GovernedFileToolNames.SessionCopy);

        var withoutLiveMetadata = FileAgentToolSet.ForWorkspace(
            [ordinary],
            new Dictionary<PanelInstanceId, FileSessionMetadata>());
        var ordinaryTools = FileAgentToolSet.ForWorkspace(
            [ordinary],
            new Dictionary<PanelInstanceId, FileSessionMetadata>
            {
                [ordinary.PanelId] = ordinary.FileMetadata!,
            });
        var governedTools = FileAgentToolSet.ForWorkspace(
            [ordinary, governed],
            new Dictionary<PanelInstanceId, FileSessionMetadata>
            {
                [ordinary.PanelId] = ordinary.FileMetadata!,
                [governed.PanelId] = governed.FileMetadata!,
            });

        Assert.Contains(
            withoutLiveMetadata,
            tool => string.Equals(
                tool.Name,
                BuiltInAgentTools.FilesList,
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            withoutLiveMetadata,
            tool => tool.Name is GovernedFileToolNames.CreateText
                or GovernedFileToolNames.ReplaceText
                or GovernedFileToolNames.Copy);
        Assert.DoesNotContain(
            ordinaryTools,
            tool => tool.Name is GovernedFileToolNames.CreateText
                or GovernedFileToolNames.ReplaceText
                or GovernedFileToolNames.Copy);
        Assert.Equal(
            [governed.PanelId.Value],
            PanelIds(governedTools, GovernedFileToolNames.CreateText));
        Assert.Equal(
            [governed.PanelId.Value],
            PanelIds(governedTools, GovernedFileToolNames.ReplaceText));
        Assert.Equal(
            [governed.PanelId.Value],
            PanelIds(governedTools, GovernedFileToolNames.Copy));
    }

    [Fact]
    public void NonHierarchicalVersionedAndIncapableScopesExposeNoTools()
    {
        var incapable = ContextPanel(
            "incapable",
            FilePanelCapability.List,
            SessionCapabilities.FilesStat);
        var objectRoot = ContextPanel(
            "object",
            FilePanelCapability.List,
            new FilePanelLocation(
                "profile-object",
                authority: null,
                new FilePanelAddress.ObjectKey("prefix")),
            SessionCapabilities.FilesList);
        var versioned = ContextPanel(
            "versioned",
            FilePanelCapability.List,
            Root("profile-versioned").WithVersion("opaque-version"),
            SessionCapabilities.FilesList);

        Assert.Empty(FileAgentToolSet.For(
            incapable,
            incapable.FileMetadata!));
        Assert.Empty(FileAgentToolSet.For(
            objectRoot,
            objectRoot.FileMetadata!));
        Assert.Empty(FileAgentToolSet.For(
            versioned,
            versioned.FileMetadata!));
    }

    [Fact]
    public void OrdinaryMutationCapabilitiesDoNotAdvertiseGovernedTools()
    {
        var panel = ContextPanel(
            "ordinary-mutations",
            FilePanelCapability.CreateDirectory
            | FilePanelCapability.Delete,
            SessionCapabilities.FilesCreateDirectory,
            SessionCapabilities.FilesDelete);

        Assert.Empty(FileAgentToolSet.For(panel, panel.FileMetadata!));
    }

    [Fact]
    public async Task Observation_tools_are_closed_bounded_and_live_capability_gated()
    {
        var panel = ContextPanel(
            "observations",
            FilePanelCapability.Search
            | FilePanelCapability.Permissions,
            SessionCapabilities.FilesSearch,
            SessionCapabilities.FilesReadAccessControl,
            SessionCapabilities.FilesTransfersRead);

        var tools = FileAgentToolSet.For(panel, panel.FileMetadata!);

        Assert.Equal(
            [
                BuiltInAgentTools.FilesSearch,
                BuiltInAgentTools.FilesAccessRead,
                BuiltInAgentTools.FilesTransfers,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        var searchSchema = tools.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.FilesSearch, StringComparison.Ordinal)).InputSchema;
        Assert.Equal(
            ["path_segments", "query", "scope", "max_results"],
            searchSchema.GetProperty("required")
                .EnumerateArray()
                .Select(item => item.GetString()), StringComparer.Ordinal);
        Assert.Empty(tools.Single(tool => string.Equals(tool.Name, BuiltInAgentTools.FilesTransfers, StringComparison.Ordinal))
            .InputSchema.GetProperty("properties")
            .EnumerateObject());

        var searchProposal = await ProposalAsync(
            BuiltInAgentTools.FilesSearch,
            """{"path_segments":["logs"],"query":"error","scope":"subtree","max_results":17}""");
        var parsedSearch = Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(
                searchProposal,
                panel,
                panel.FileMetadata!));
        var search = Assert.IsType<FileAgentIntent.Search>(parsedSearch.Intent);
        Assert.Equal("error", search.Query);
        Assert.Equal(FilePanelDiscoveryScope.Subtree, search.Scope);
        Assert.Equal(17, search.MaximumResults);

        var accessProposal = await ProposalAsync(
            BuiltInAgentTools.FilesAccessRead,
            """{"path_segments":[]}""");
        Assert.IsType<FileAgentIntent.AccessRead>(
            Assert.IsType<FileAgentIntentResult.Parsed>(
                FileAgentToolParser.Parse(
                    accessProposal,
                    panel,
                    panel.FileMetadata!)).Intent);
        var transfersProposal = await ProposalAsync(
            BuiltInAgentTools.FilesTransfers,
            "{}");
        Assert.IsType<FileAgentIntent.Transfers>(
            Assert.IsType<FileAgentIntentResult.Parsed>(
                FileAgentToolParser.Parse(
                    transfersProposal,
                    panel,
                    panel.FileMetadata!)).Intent);

        var searchWithoutProviderSupport = ContextPanel(
            "search-unavailable",
            FilePanelCapability.List,
            SessionCapabilities.FilesSearch);
        Assert.Empty(FileAgentToolSet.For(
            searchWithoutProviderSupport,
            searchWithoutProviderSupport.FileMetadata!));

        foreach (var arguments in new[]
        {
            """{"path_segments":[],"query":" error","scope":"subtree","max_results":1}""",
            """{"path_segments":[],"query":"error","scope":"all","max_results":1}""",
            """{"path_segments":[],"query":"error","scope":"subtree","max_results":0}""",
            """{"path_segments":[],"query":"error","scope":"subtree","max_results":1,"show_hidden":true}""",
        })
        {
            var proposal = await ProposalAsync(
                BuiltInAgentTools.FilesSearch,
                arguments);
            Assert.Equal(
                "invalid_tool_arguments",
                Assert.IsType<FileAgentIntentResult.Rejected>(
                    FileAgentToolParser.Parse(
                        proposal,
                        panel,
                        panel.FileMetadata!)).StableCode);
        }
    }

    [Theory]
    [InlineData("""{"path_segments":[".."]}""")]
    [InlineData("""{"path_segments":["."]}""")]
    [InlineData("""{"path_segments":["/etc"]}""")]
    [InlineData("""{"path_segments":["..\\secret"]}""")]
    [InlineData("""{"path_segments":["safe\u0000secret"]}""")]
    [InlineData("""{"path_segments":["safe"],"absolute_path":"/etc/passwd"}""")]
    [InlineData("""{"path_segments":["safe"],"profile_id":"other"}""")]
    [InlineData("""{"path_segments":["safe"],"continuation":"opaque"}""")]
    [InlineData("""{"path_segments":["safe"],"page_size":1000}""")]
    [InlineData("""{"path_segments":["safe"],"show_hidden":true}""")]
    [InlineData("""{"path_segments":["safe"],"maximum_bytes":999999}""")]
    public async Task ParserRejectsPathWideningAndUnknownFields(
        string arguments)
    {
        var panel = ContextPanel(
            "parser",
            FilePanelCapability.List,
            SessionCapabilities.FilesList);
        var proposal = await ProposalAsync(
            BuiltInAgentTools.FilesList,
            arguments);

        var result = Assert.IsType<FileAgentIntentResult.Rejected>(
            FileAgentToolParser.Parse(
                proposal,
                panel,
                panel.FileMetadata!));

        Assert.Equal("invalid_tool_arguments", result.StableCode);
    }

    [Fact]
    public async Task ExactAndBroadParsersKeepPanelIdentityHostOwned()
    {
        var panel = ContextPanel(
            "selection",
            FilePanelCapability.List,
            SessionCapabilities.FilesList);
        var exactProposal = await ProposalAsync(
            BuiltInAgentTools.FilesList,
            """{"path_segments":[]}""");

        var exact = Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(
                exactProposal,
                panel,
                panel.FileMetadata!));
        Assert.Equal(panel.PanelId, exact.PanelId);
        Assert.Empty(
            Assert.IsType<FileAgentIntent.List>(
                exact.Intent).RelativePath);

        var selectedProposal = await ProposalAsync(
            BuiltInAgentTools.FilesList,
            JsonSerializer.Serialize(new
            {
                path_segments = Array.Empty<string>(),
                panel_id = panel.PanelId.Value,
            }));
        var exactWithPanelId =
            Assert.IsType<FileAgentIntentResult.Rejected>(
                FileAgentToolParser.Parse(
                    selectedProposal,
                    panel,
                    panel.FileMetadata!));
        Assert.Equal(
            "invalid_tool_arguments",
            exactWithPanelId.StableCode);

        AgentContextPanel[] broadPanels = [panel];
        var metadata =
            new Dictionary<PanelInstanceId, FileSessionMetadata>
            {
                [panel.PanelId] = panel.FileMetadata!,
            };
        var broad = Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(
                selectedProposal,
                broadPanels,
                metadata));
        Assert.Equal(panel.PanelId, broad.PanelId);

        var broadWithoutPanel =
            Assert.IsType<FileAgentIntentResult.Rejected>(
                FileAgentToolParser.Parse(
                    exactProposal,
                    broadPanels,
                    metadata));
        Assert.Equal(
            "invalid_tool_arguments",
            broadWithoutPanel.StableCode);
    }

    [Fact]
    public async Task ReadsAndMutationsRequireAPathButListAndStatAllowTheRoot()
    {
        var panel = ContextPanel(
            "root",
            FilePanelCapability.List
            | FilePanelCapability.Stat
            | FilePanelCapability.RangedRead
            | FilePanelCapability.CreateDirectory
            | FilePanelCapability.Delete
            | FilePanelCapability.GovernedCreateDirectory
            | FilePanelCapability.GovernedDelete,
            SessionCapabilities.FilesList,
            SessionCapabilities.FilesStat,
            SessionCapabilities.FilesPreview,
            SessionCapabilities.FilesCreateDirectory,
            SessionCapabilities.FilesDelete);

        var list = await ProposalAsync(
            BuiltInAgentTools.FilesList,
            """{"path_segments":[]}""");
        var stat = await ProposalAsync(
            BuiltInAgentTools.FilesStat,
            """{"path_segments":[]}""");
        var read = await ProposalAsync(
            BuiltInAgentTools.FilesRead,
            """{"path_segments":[]}""");
        var createDirectory = await ProposalAsync(
            BuiltInAgentTools.FilesCreateDirectory,
            """{"path_segments":[]}""");
        var delete = await ProposalAsync(
            BuiltInAgentTools.FilesDelete,
            """{"path_segments":[]}""");

        Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(list, panel, panel.FileMetadata!));
        Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(stat, panel, panel.FileMetadata!));
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<FileAgentIntentResult.Rejected>(
                FileAgentToolParser.Parse(
                    read,
                    panel,
                    panel.FileMetadata!)).StableCode);
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<FileAgentIntentResult.Rejected>(
                FileAgentToolParser.Parse(
                    createDirectory,
                    panel,
                    panel.FileMetadata!)).StableCode);
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<FileAgentIntentResult.Rejected>(
                FileAgentToolParser.Parse(
                    delete,
                    panel,
                    panel.FileMetadata!)).StableCode);
    }

    [Fact]
    public async Task MutationParserProducesOnlyTypedPathsAndRejectsHostSemantics()
    {
        var panel = ContextPanel(
            "mutation-parser",
            FilePanelCapability.CreateDirectory
            | FilePanelCapability.Rename
            | FilePanelCapability.Delete
            | FilePanelCapability.GovernedCreateDirectory
            | FilePanelCapability.GovernedRename
            | FilePanelCapability.GovernedDelete,
            SessionCapabilities.FilesCreateDirectory,
            SessionCapabilities.FilesRename,
            SessionCapabilities.FilesDelete);
        var createDirectory = await ProposalAsync(
            BuiltInAgentTools.FilesCreateDirectory,
            """{"path_segments":["deploy","current"]}""");
        var entryReference = new string('A', AgentFileEntryReference.EncodedLength);
        var delete = await ProposalAsync(
            BuiltInAgentTools.FilesDelete,
            $$"""{"path_segments":["deploy","old"],"entry_ref":"{{entryReference}}"}""");
        var move = await ProposalAsync(
            BuiltInAgentTools.FilesMove,
            $$"""{"source_path_segments":["deploy","draft"],"destination_path_segments":["archive","draft"],"entry_ref":"{{entryReference}}"}""");

        var parsedCreate = Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(
                createDirectory,
                panel,
                panel.FileMetadata!));
        var parsedDelete = Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(
                delete,
                panel,
                panel.FileMetadata!));
        var parsedMove = Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(
                move,
                panel,
                panel.FileMetadata!));

        Assert.Equal(
            ["deploy", "current"],
            Assert.IsType<FileAgentIntent.CreateDirectory>(
                    parsedCreate.Intent)
                .RelativePath
                .Select(segment => segment.Value), StringComparer.Ordinal);
        Assert.Equal(
            ["deploy", "old"],
            Assert.IsType<FileAgentIntent.Delete>(parsedDelete.Intent)
                .RelativePath
                .Select(segment => segment.Value), StringComparer.Ordinal);
        var moveIntent = Assert.IsType<FileAgentIntent.Move>(parsedMove.Intent);
        Assert.Equal(
            ["deploy", "draft"],
            moveIntent.RelativePath.Select(segment => segment.Value), StringComparer.Ordinal);
        Assert.Equal(
            ["archive", "draft"],
            moveIntent.DestinationRelativePath.Select(segment => segment.Value), StringComparer.Ordinal);

        var moveSchema = FileAgentToolSet.For(panel, panel.FileMetadata!)
            .Single(tool => string.Equals(tool.Name, BuiltInAgentTools.FilesMove, StringComparison.Ordinal))
            .InputSchema;
        Assert.Equal(
            ["source_path_segments", "destination_path_segments", "entry_ref"],
            moveSchema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.All(
            ["source_path_segments", "destination_path_segments"],
            property => Assert.Equal(
                1,
                moveSchema.GetProperty("properties")
                    .GetProperty(property)
                    .GetProperty("minItems")
                    .GetInt32()));

        var samePathMove = await ProposalAsync(
            BuiltInAgentTools.FilesMove,
            $$"""{"source_path_segments":["same"],"destination_path_segments":["same"],"entry_ref":"{{entryReference}}"}""");
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<FileAgentIntentResult.Rejected>(
                FileAgentToolParser.Parse(
                    samePathMove,
                    panel,
                    panel.FileMetadata!)).StableCode);

        var recursiveDelete = Assert.IsType<FileAgentIntentResult.Parsed>(
            FileAgentToolParser.Parse(
                await ProposalAsync(
                    BuiltInAgentTools.FilesDelete,
                    $$"""{"path_segments":["old"],"recursive":true,"entry_ref":"{{entryReference}}"}"""),
                panel,
                panel.FileMetadata!));
        Assert.True(Assert.IsType<FileAgentIntent.Delete>(recursiveDelete.Intent).Recursive);

        foreach (var forbiddenArguments in new[]
        {
            """{"path_segments":["old"],"precondition":"any"}""",
            """{"path_segments":["old"],"version":"opaque"}""",
            """{"path_segments":["old"],"retry":true}""",
        })
        {
            var proposal = await ProposalAsync(
                BuiltInAgentTools.FilesDelete,
                forbiddenArguments);
            Assert.Equal(
                "invalid_tool_arguments",
                Assert.IsType<FileAgentIntentResult.Rejected>(
                    FileAgentToolParser.Parse(
                        proposal,
                        panel,
                        panel.FileMetadata!)).StableCode);
        }
    }

    private static readonly string[] ForbiddenProviderFields =
    [
        "absolute_path",
        "profile_id",
        "authority",
        "page_size",
        "continuation",
        "show_hidden",
        "maximum_bytes",
        "precondition",
        "version",
        "retry",
    ];

    private static string[] PanelIds(
        ImmutableArray<AgentToolDefinition> tools,
        string toolName) =>
        [.. tools
            .Single(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))
            .InputSchema
            .GetProperty("properties")
            .GetProperty("panel_id")
            .GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!)];

    private static AgentContextPanel ContextPanel(
        string suffix,
        FilePanelCapability providerCapabilities,
        params string[] sessionCapabilities) =>
        ContextPanel(
            suffix,
            providerCapabilities,
            Root($"profile-{suffix}"),
            sessionCapabilities);

    private static AgentContextPanel ContextPanel(
        string suffix,
        FilePanelCapability providerCapabilities,
        FilePanelLocation root,
        params string[] sessionCapabilities)
    {
        var sessionId = new SessionId($"session-{suffix}");
        var windowId = new WindowInstanceId($"window-{suffix}");
        var workspaceId = new WorkspaceInstanceId($"workspace-{suffix}");
        var tabId = new TabInstanceId($"tab-{suffix}");
        var panelId = new PanelInstanceId($"panel-{suffix}");
        var panel = new PanelInstance(
            panelId,
            PanelKind.FileViewer,
            $"Files {suffix}",
            sessionId);
        var tab = new TabInstance(tabId, "Files", [panel], panelId);
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
            PanelKind.FileViewer,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                windowId,
                workspaceId,
                tabId,
                panelId),
            new CapabilitySet(sessionCapabilities),
            Revision: 4,
            HasActiveWork: false,
            StatusDetail: "Ready",
            FileMetadata: new FileSessionMetadata(
                root,
                providerCapabilities,
                maximumListPageSize: 100,
                maximumPreviewBytes: 64 * 1024));
        return AgentContextPanel.ForGraphPanel(
            graph,
            tabId,
            panelId,
            descriptor);
    }

    private static FilePanelLocation Root(string profileId) =>
        new(
            profileId,
            authority: null,
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(
                [
                    new FilePanelPathSegment("srv"),
                    new FilePanelPathSegment("operations"),
                ])));

    private static async Task<AgentToolProposal> ProposalAsync(
        string name,
        string arguments)
    {
        var session = new NativeAgentSession(
            new AgentRunId("file-contract"));
        var result = await session.RunTurnAsync(
            "Use the file tool.",
            [Tool(name)],
            new ToolProvider(name, arguments),
            CancellationToken.None);
        Assert.True(result.Succeeded);
        return Assert.Single(result.ToolProposals);
    }

    private static AgentToolDefinition Tool(string name) =>
        new(
            name,
            "Test file tool.",
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """u8.ToArray());

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
                "file-call",
                ProviderToolName.FromInternal(name));
            yield return new AgentProviderEvent.ToolCallArgumentsDelta(
                0,
                arguments);
            yield return new AgentProviderEvent.ToolCallCompleted(0);
            yield return new AgentProviderEvent.ResponseCompleted(
                AgentProviderStopReason.ToolUse);
            await Task.CompletedTask;
        }
    }
}

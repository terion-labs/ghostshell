using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Browser;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class BrowserAgentToolContractTests
{
    [Fact]
    public void ExactPanelSchemasExposeOnlySupportedBrowserOperations()
    {
        var panel = ContextPanel(
            "exact",
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserWait,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserAgentInputBarrier);

        var tools = BrowserAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.BrowserReadState,
                BuiltInAgentTools.BrowserSnapshot,
                BuiltInAgentTools.BrowserWait,
                BuiltInAgentTools.BrowserClick,
                BuiltInAgentTools.BrowserFill,
                BuiltInAgentTools.BrowserCheck,
                BuiltInAgentTools.BrowserNavigate,
                BuiltInAgentTools.BrowserBack,
                BuiltInAgentTools.BrowserForward,
                BuiltInAgentTools.BrowserReload,
                BuiltInAgentTools.BrowserStop,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.All(
            tools,
            tool =>
            {
                Assert.False(
                    tool.InputSchema
                        .GetProperty("additionalProperties")
                        .GetBoolean());
                Assert.DoesNotContain(
                    "panel_id",
                    tool.InputSchema.GetRawText(),
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    "session",
                    tool.InputSchema.GetRawText(),
                    StringComparison.OrdinalIgnoreCase);
            });

        var navigate = tools.Single(
            tool => string.Equals(tool.Name, BuiltInAgentTools.BrowserNavigate, StringComparison.Ordinal));
        var schema = navigate.InputSchema;
        var properties = schema.GetProperty("properties");
        Assert.Equal(["url"], properties.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        Assert.Equal(
            2_048,
            properties
                .GetProperty("url")
                .GetProperty("maxLength")
                .GetInt32());
        Assert.Equal(
            ["url"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);

        var click = tools.Single(
            tool => string.Equals(tool.Name, BuiltInAgentTools.BrowserClick, StringComparison.Ordinal));
        var clickSchema = click.InputSchema;
        var clickProperties = clickSchema.GetProperty("properties");
        var reference = clickProperties.GetProperty("reference");
        Assert.Equal("string", reference.GetProperty("type").GetString());
        Assert.Equal(
            "^[A-Za-z0-9_-]+$",
            reference.GetProperty("pattern").GetString());
        Assert.Equal(1, reference.GetProperty("minLength").GetInt32());
        Assert.Equal(128, reference.GetProperty("maxLength").GetInt32());
        Assert.Equal(
            "integer",
            clickProperties
                .GetProperty("document_revision")
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            0,
            clickProperties
                .GetProperty("document_revision")
                .GetProperty("minimum")
                .GetInt64());
        Assert.Equal(
            ["reference", "document_revision"],
            clickSchema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);

        var fill = tools.Single(
            tool => string.Equals(tool.Name, BuiltInAgentTools.BrowserFill, StringComparison.Ordinal));
        var fillSchema = fill.InputSchema;
        var fillProperties = fillSchema.GetProperty("properties");
        Assert.Equal(
            ["reference", "document_revision", "text"],
            fillProperties.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal);
        Assert.Equal(
            2_048,
            fillProperties.GetProperty("text")
                .GetProperty("maxLength")
                .GetInt32());
        Assert.Equal(
            ["reference", "document_revision", "text"],
            fillSchema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        var check = tools.Single(
            tool => string.Equals(tool.Name, BuiltInAgentTools.BrowserCheck, StringComparison.Ordinal));
        Assert.Equal(
            clickSchema.GetRawText(),
            check.InputSchema.GetRawText());
        Assert.True(BrowserAgentToolSet.SupportsMutations(panel));
    }

    [Fact]
    public void InactiveNonBrowserAndIncapablePanelsExposeNoTools()
    {
        var noCapabilities = ContextPanel("none");
        var closed = ContextPanel(
            "closed",
            SessionLifecycle.Closing,
            SessionCapabilities.BrowserReadState);
        var terminal = ContextPanel(
            "terminal",
            PanelKind.Terminal,
            SessionLifecycle.Active,
            SessionCapabilities.BrowserReadState);

        Assert.Empty(BrowserAgentToolSet.For(noCapabilities));
        Assert.Empty(BrowserAgentToolSet.For(closed));
        Assert.Empty(BrowserAgentToolSet.For(terminal));
        Assert.False(BrowserAgentToolSet.SupportsMutations(noCapabilities));
        Assert.False(
            BrowserAgentToolSet.SupportsMutations(
                ContextPanel(
                    "snapshot-only",
                    SessionCapabilities.BrowserSnapshot)));
    }

    [Fact]
    public void NavigationToolsRequireTheRendererOriginGuardCapability()
    {
        var panel = ContextPanel(
            "unguarded",
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserAgentInputBarrier);

        var tools = BrowserAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.BrowserReadState,
                BuiltInAgentTools.BrowserSnapshot,
                BuiltInAgentTools.BrowserStop,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.True(BrowserAgentToolSet.SupportsMutations(panel));
        Assert.False(BrowserAgentToolSet.SupportsMutations(
            ContextPanel(
                "unguarded-navigation-only",
                SessionCapabilities.BrowserNavigate,
                SessionCapabilities.BrowserAgentInputBarrier)));
        Assert.False(BrowserAgentToolSet.SupportsMutations(
            ContextPanel(
                "unguarded-click-only",
                SessionCapabilities.BrowserClick,
                SessionCapabilities.BrowserAgentInputBarrier)));
        Assert.False(BrowserAgentToolSet.SupportsMutations(
            ContextPanel(
                "unguarded-fill-only",
                SessionCapabilities.BrowserFill,
                SessionCapabilities.BrowserAgentInputBarrier)));
        Assert.False(BrowserAgentToolSet.SupportsMutations(
            ContextPanel(
                "unguarded-check-only",
                SessionCapabilities.BrowserCheck,
                SessionCapabilities.BrowserAgentInputBarrier)));
    }

    [Fact]
    public void MutationToolsRequireThePhysicalHumanInputBarrier()
    {
        var panel = ContextPanel(
            "barrierless",
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserSnapshot,
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserReload,
            SessionCapabilities.BrowserStop,
            SessionCapabilities.BrowserOriginGuard);

        var tools = BrowserAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.BrowserReadState,
                BuiltInAgentTools.BrowserSnapshot,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.False(BrowserAgentToolSet.SupportsMutations(panel));
    }

    [Fact]
    public void ProductionProfileExposesTheConformantSemanticToolSet()
    {
        var panel = ContextPanel(
            "production",
            [.. BrowserCapabilityProfile.Production.Capabilities.Values]);

        var tools = BrowserAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.BrowserReadState,
                BuiltInAgentTools.BrowserSnapshot,
                BuiltInAgentTools.BrowserWait,
                BuiltInAgentTools.BrowserClick,
                BuiltInAgentTools.BrowserFill,
                BuiltInAgentTools.BrowserCheck,
                BuiltInAgentTools.BrowserMouse,
                BuiltInAgentTools.BrowserKey,
                BuiltInAgentTools.BrowserScroll,
                BuiltInAgentTools.BrowserNavigate,
                BuiltInAgentTools.BrowserBack,
                BuiltInAgentTools.BrowserForward,
                BuiltInAgentTools.BrowserReload,
                BuiltInAgentTools.BrowserStop,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
    }

    [Fact]
    public void BroadSchemasEnumerateOnlyPanelsEligibleForEachOperation()
    {
        var read = ContextPanel(
            "read",
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserSnapshot);
        var navigate = ContextPanel(
            "navigate",
            SessionCapabilities.BrowserReadState,
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserAgentInputBarrier);
        var click = ContextPanel(
            "click",
            SessionCapabilities.BrowserClick,
            SessionCapabilities.BrowserFill,
            SessionCapabilities.BrowserCheck,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserAgentInputBarrier);
        var history = ContextPanel(
            "history",
            SessionCapabilities.BrowserBack,
            SessionCapabilities.BrowserForward,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserAgentInputBarrier);

        var tools = BrowserAgentToolSet.For([read, navigate, click, history]);

        Assert.Equal(
            [read.PanelId.Value, navigate.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.BrowserReadState));
        Assert.Equal(
            [read.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.BrowserSnapshot));
        Assert.Equal(
            [navigate.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.BrowserNavigate));
        Assert.Equal(
            [click.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.BrowserClick));
        Assert.Equal(
            [click.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.BrowserFill));
        Assert.Equal(
            [click.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.BrowserCheck));
        var broadClick = tools.Single(
            tool => string.Equals(tool.Name, BuiltInAgentTools.BrowserClick, StringComparison.Ordinal));
        Assert.Equal(
            ["reference", "document_revision", "panel_id"],
            broadClick.InputSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            [click.PanelId.Value],
            broadClick.InputSchema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            [history.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.BrowserBack));
        Assert.Equal(
            [history.PanelId.Value],
            PanelIds(tools, BuiltInAgentTools.BrowserForward));
        Assert.DoesNotContain(
            tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.BrowserReload, StringComparison.Ordinal));
        Assert.DoesNotContain(
            tools,
            tool => string.Equals(tool.Name, BuiltInAgentTools.BrowserStop, StringComparison.Ordinal));
        Assert.All(
            tools,
            tool => Assert.Contains(
                tool.InputSchema.GetProperty("required").EnumerateArray(),
                requirement => string.Equals(requirement.GetString(), "panel_id", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            "panel_id",
            BrowserAgentToolSet.For(read)
                .Single(
                    tool => string.Equals(tool.Name
, BuiltInAgentTools.BrowserReadState, StringComparison.Ordinal))
                .InputSchema
                .GetRawText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneItemBroadScopeStillRequiresExplicitPanelSelection()
    {
        var panel = ContextPanel(
            "one-broad",
            SessionCapabilities.BrowserReadState);
        var tool = Assert.Single(BrowserAgentToolSet.For([panel]));
        var schema = tool.InputSchema;

        Assert.Equal(
            [panel.PanelId.Value],
            schema
                .GetProperty("properties")
                .GetProperty("panel_id")
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            ["panel_id"],
            schema.GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);

        var omittedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserReadState,
            "{}");
        var omitted = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(omittedProposal, [panel]));
        Assert.Equal("invalid_tool_arguments", omitted.StableCode);

        var selectedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserReadState,
            JsonSerializer.Serialize(new
            {
                panel_id = panel.PanelId.Value,
            }));
        var selected = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(selectedProposal, [panel]));
        Assert.Equal(panel.PanelId, selected.PanelId);
    }

    [Theory]
    [InlineData(BuiltInAgentTools.BrowserReadState, typeof(BrowserAgentIntent.ReadState))]
    [InlineData(BuiltInAgentTools.BrowserSnapshot, typeof(BrowserAgentIntent.Snapshot))]
    [InlineData(BuiltInAgentTools.BrowserBack, typeof(BrowserAgentIntent.Back))]
    [InlineData(BuiltInAgentTools.BrowserForward, typeof(BrowserAgentIntent.Forward))]
    [InlineData(BuiltInAgentTools.BrowserReload, typeof(BrowserAgentIntent.Reload))]
    [InlineData(BuiltInAgentTools.BrowserStop, typeof(BrowserAgentIntent.Stop))]
    public async Task ParserAcceptsTheClosedArgumentFreeIntentSet(
        string toolName,
        Type intentType)
    {
        var proposal = await ProposalAsync(toolName, "{}");

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.IsType(intentType, parsed.Intent);
        Assert.Null(parsed.PanelId);
    }

    [Fact]
    public async Task SnapshotParserAcceptsBrowseStyleNarrowingOptions()
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserSnapshot,
            """
            {
              "interactive_only": true,
              "filter": "YouTube result",
              "max_depth": 6
            }
            """);

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(proposal));
        var snapshot = Assert.IsType<BrowserAgentIntent.Snapshot>(
            parsed.Intent);

        Assert.True(snapshot.InteractiveOnly);
        Assert.Equal("YouTube result", snapshot.Filter);
        Assert.Equal(6, snapshot.MaximumDepth);
    }

    [Theory]
    [InlineData(
        "{\"timeout_ms\":3600000,\"delay_ms\":1}",
        typeof(BrowserWaitCondition.Delay))]
    [InlineData(
        "{\"timeout_ms\":1000,\"load_state\":\"ready\"}",
        typeof(BrowserWaitCondition.LoadState))]
    [InlineData(
        "{\"timeout_ms\":1000,\"url_pattern\":\"https://example.test/*\"}",
        typeof(BrowserWaitCondition.UrlPattern))]
    [InlineData(
        "{\"timeout_ms\":1000,\"text\":\"Ready 😀\"}",
        typeof(BrowserWaitCondition.Text))]
    [InlineData(
        "{\"timeout_ms\":1000,\"reference\":\"button_1\",\"document_revision\":7,\"ref_state\":\"enabled\",\"expected\":true}",
        typeof(BrowserWaitCondition.ElementState))]
    [InlineData(
        "{\"timeout_ms\":1000,\"after_document_revision\":7}",
        typeof(BrowserWaitCondition.DocumentRevision))]
    [InlineData(
        "{\"timeout_ms\":1000,\"network_idle_ms\":500}",
        typeof(BrowserWaitCondition.NetworkIdle))]
    public async Task WaitParserAcceptsExactlyOneClosedCondition(
        string arguments,
        Type conditionType)
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserWait,
            arguments);

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(proposal));
        var wait = Assert.IsType<BrowserAgentIntent.Wait>(parsed.Intent);

        Assert.IsType(conditionType, wait.Condition);
        Assert.InRange(
            wait.Timeout,
            TimeSpan.FromMilliseconds(1),
            BrowserWaitRequest.MaximumTimeout);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"timeout_ms\":0,\"delay_ms\":1}")]
    [InlineData("{\"timeout_ms\":3600001,\"delay_ms\":1}")]
    [InlineData("{\"timeout_ms\":1000,\"delay_ms\":1001}")]
    [InlineData("{\"timeout_ms\":1000,\"network_idle_ms\":1001}")]
    [InlineData("{\"timeout_ms\":1000,\"delay_ms\":1,\"text\":\"Ready\"}")]
    [InlineData("{\"timeout_ms\":1000,\"text\":\"Ready\",\"extra\":true}")]
    public async Task WaitParserRejectsUnboundedAmbiguousAndMalformedRequests(
        string arguments)
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserWait,
            arguments);

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public void WaitToolUsesTheLongLivedExecutionDeadline()
    {
        Assert.True(BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.BrowserWait,
            out var descriptor));

        Assert.Equal(TimeSpan.FromMinutes(61), descriptor!.MaximumExecutionLifetime);
    }

    [Theory]
    [InlineData("https://example.test/operations?view=all#active")]
    [InlineData("http://localhost:8080/health")]
    [InlineData("about:blank")]
    public async Task NavigateParserReturnsAValidatedAddress(string url)
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserNavigate,
            JsonSerializer.Serialize(new
            {
                url,
            }));

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(proposal));
        var navigate = Assert.IsType<BrowserAgentIntent.Navigate>(
            parsed.Intent);

        Assert.Equal(url, navigate.Address.ToString());
    }

    [Fact]
    public async Task ClickParserReturnsTheExactOpaqueReferenceAndDocumentRevision()
    {
        var maximumReference =
            new string('r', BrowserElementReferenceId.MaximumValueBytes);
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserClick,
            JsonSerializer.Serialize(new
            {
                reference = maximumReference,
                document_revision = long.MaxValue,
            }));

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(proposal));
        var click = Assert.IsType<BrowserAgentIntent.Click>(
            parsed.Intent);

        Assert.Equal(maximumReference, click.Reference.Value);
        Assert.Equal(long.MaxValue, click.DocumentRevision);
    }

    [Fact]
    public async Task CheckParserReturnsTheExactOpaqueReferenceAndDocumentRevision()
    {
        var maximumReference =
            new string('r', BrowserElementReferenceId.MaximumValueBytes);
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserCheck,
            JsonSerializer.Serialize(new
            {
                reference = maximumReference,
                document_revision = long.MaxValue,
            }));

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(proposal));
        var check = Assert.IsType<BrowserAgentIntent.Check>(
            parsed.Intent);

        Assert.Equal(maximumReference, check.Reference.Value);
        Assert.Equal(long.MaxValue, check.DocumentRevision);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reference\":null,\"document_revision\":1}")]
    [InlineData("{\"reference\":42,\"document_revision\":1}")]
    [InlineData("{\"reference\":\"\",\"document_revision\":1}")]
    [InlineData("{\"reference\":\"element.1\",\"document_revision\":1}")]
    [InlineData("{\"reference\":\"élément\",\"document_revision\":1}")]
    [InlineData("{\"reference\":\"element_1\",\"document_revision\":-1}")]
    [InlineData("{\"reference\":\"element_1\",\"document_revision\":1.5}")]
    [InlineData("{\"reference\":\"element_1\",\"document_revision\":\"1\"}")]
    [InlineData("{\"reference\":\"element_1\",\"document_revision\":1,\"extra\":true}")]
    public async Task ClickParserRejectsMissingUnknownAndMalformedFields(
        string arguments)
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserClick,
            arguments);

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reference\":null,\"document_revision\":1}")]
    [InlineData("{\"reference\":42,\"document_revision\":1}")]
    [InlineData("{\"reference\":\"\",\"document_revision\":1}")]
    [InlineData("{\"reference\":\"element.1\",\"document_revision\":1}")]
    [InlineData("{\"reference\":\"élément\",\"document_revision\":1}")]
    [InlineData("{\"reference\":\"element_1\",\"document_revision\":-1}")]
    [InlineData("{\"reference\":\"element_1\",\"document_revision\":1.5}")]
    [InlineData("{\"reference\":\"element_1\",\"document_revision\":\"1\"}")]
    [InlineData("{\"reference\":\"element_1\",\"document_revision\":1,\"extra\":true}")]
    public async Task CheckParserRejectsMissingUnknownAndMalformedFields(
        string arguments)
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserCheck,
            arguments);

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task ClickParserRejectsReferencesLongerThanItsProviderBoundary()
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserClick,
            JsonSerializer.Serialize(new
            {
                reference = new string(
                    'r',
                    BrowserElementReferenceId.MaximumValueBytes + 1),
                document_revision = 1,
            }));

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task FillParserReturnsExactTextReferenceAndDocumentRevision()
    {
        const string Text = "first line\tvalue\r\nsecond line 😀";
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserFill,
            JsonSerializer.Serialize(new
            {
                reference = "editable_1",
                document_revision = long.MaxValue,
                text = Text,
            }));

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(proposal));
        var fill = Assert.IsType<BrowserAgentIntent.Fill>(parsed.Intent);

        Assert.Equal("editable_1", fill.Reference.Value);
        Assert.Equal(long.MaxValue, fill.DocumentRevision);
        Assert.Equal(Text, fill.Text);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"reference\":\"field_1\",\"document_revision\":1}")]
    [InlineData("{\"reference\":null,\"document_revision\":1,\"text\":\"value\"}")]
    [InlineData("{\"reference\":\"field.1\",\"document_revision\":1,\"text\":\"value\"}")]
    [InlineData("{\"reference\":\"field_1\",\"document_revision\":-1,\"text\":\"value\"}")]
    [InlineData("{\"reference\":\"field_1\",\"document_revision\":1.5,\"text\":\"value\"}")]
    [InlineData("{\"reference\":\"field_1\",\"document_revision\":1,\"text\":null}")]
    [InlineData("{\"reference\":\"field_1\",\"document_revision\":1,\"text\":42}")]
    [InlineData("{\"reference\":\"field_1\",\"document_revision\":1,\"text\":\"bad\\u0000control\"}")]
    [InlineData("{\"reference\":\"field_1\",\"document_revision\":1,\"text\":\"value\",\"extra\":true}")]
    public async Task FillParserRejectsMissingUnknownAndMalformedFields(
        string arguments)
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserFill,
            arguments);

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task FillParserEnforcesTheUtf8ByteBoundary()
    {
        var acceptedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserFill,
            JsonSerializer.Serialize(new
            {
                reference = "field_1",
                document_revision = 1,
                text = new string('é', 1_024),
            }));
        var rejectedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserFill,
            JsonSerializer.Serialize(new
            {
                reference = "field_1",
                document_revision = 1,
                text = new string('é', 1_025),
            }));

        Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(acceptedProposal));
        Assert.Equal(
            "invalid_tool_arguments",
            Assert.IsType<BrowserAgentIntentResult.Rejected>(
                BrowserAgentToolParser.Parse(rejectedProposal)).StableCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"url\":null}")]
    [InlineData("{\"url\":42}")]
    [InlineData("{\"url\":\"\"}")]
    [InlineData("{\"url\":\" https://example.test\"}")]
    [InlineData("{\"url\":\"https://example.test \"}")]
    [InlineData("{\"url\":\"relative/path\"}")]
    [InlineData("{\"url\":\"file:///etc/passwd\"}")]
    [InlineData("{\"url\":\"javascript:alert(1)\"}")]
    [InlineData("{\"url\":\"https://user:secret@example.test\"}")]
    [InlineData("{\"url\":\"https://example.test\",\"extra\":true}")]
    [InlineData("{\"address\":\"https://example.test\"}")]
    public async Task NavigateParserRejectsMissingUnknownAndMalformedFields(
        string arguments)
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserNavigate,
            arguments);

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task NavigateParserRejectsUrlsLongerThanItsProviderBoundary()
    {
        var url = "https://example.test/" + new string('a', 2_029);
        Assert.Equal(2_050, url.Length);
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserNavigate,
            JsonSerializer.Serialize(new
            {
                url,
            }));

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task ArgumentFreeToolsRejectUnknownFields()
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserReload,
            "{\"force\":true}");

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task NativeKernelRejectsDuplicateBrowserFields()
    {
        var result = await RunProviderAsync(
            BuiltInAgentTools.BrowserNavigate,
            """
            {
              "url": "https://first.example",
              "url": "https://second.example"
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            AgentTurnErrorCode.InvalidProviderStream,
            result.ErrorCode);
        Assert.Empty(result.ToolProposals);
    }

    [Fact]
    public async Task NativeKernelRejectsDuplicateBrowserFillFields()
    {
        var result = await RunProviderAsync(
            BuiltInAgentTools.BrowserFill,
            """
            {
              "reference": "field_1",
              "reference": "field_2",
              "document_revision": 1,
              "text": "value"
            }
            """);

        Assert.False(result.Succeeded);
        Assert.Equal(
            AgentTurnErrorCode.InvalidProviderStream,
            result.ErrorCode);
        Assert.Empty(result.ToolProposals);
    }

    [Fact]
    public async Task BroadParserRoutesOnlyToAnEligibleExactPanel()
    {
        var read = ContextPanel(
            "read",
            SessionCapabilities.BrowserReadState);
        var navigate = ContextPanel(
            "navigate",
            SessionCapabilities.BrowserNavigate,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserAgentInputBarrier);
        AgentContextPanel[] scope = [read, navigate];
        var acceptedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserNavigate,
            JsonSerializer.Serialize(new
            {
                url = "https://example.test/run",
                panel_id = navigate.PanelId.Value,
            }));

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(acceptedProposal, scope));
        Assert.Equal(navigate.PanelId, parsed.PanelId);
        Assert.IsType<BrowserAgentIntent.Navigate>(parsed.Intent);

        var unavailableProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserNavigate,
            JsonSerializer.Serialize(new
            {
                url = "https://example.test/run",
                panel_id = read.PanelId.Value,
            }));
        var unavailable = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(unavailableProposal, scope));
        Assert.Equal("invalid_tool_arguments", unavailable.StableCode);

        var omittedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserNavigate,
            "{\"url\":\"https://example.test/run\"}");
        var omitted = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(omittedProposal, scope));
        Assert.Equal("invalid_tool_arguments", omitted.StableCode);
    }

    [Fact]
    public async Task ExactPanelParserUsesOnlyItsHostOwnedPanelIdentity()
    {
        var panel = ContextPanel(
            "exact",
            SessionCapabilities.BrowserReadState);
        var acceptedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserReadState,
            "{}");
        var accepted = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(acceptedProposal, panel));
        Assert.Equal(panel.PanelId, accepted.PanelId);

        var selectedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserReadState,
            JsonSerializer.Serialize(new
            {
                panel_id = panel.PanelId.Value,
            }));

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(selectedProposal, panel));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
    }

    [Fact]
    public async Task SnapshotParserUsesExactAndBroadPanelSelectionRules()
    {
        var eligible = ContextPanel(
            "snapshot",
            SessionCapabilities.BrowserSnapshot);
        var ineligible = ContextPanel(
            "state-only",
            SessionCapabilities.BrowserReadState);
        var exactProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserSnapshot,
            "{}");

        var exact = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(exactProposal, eligible));
        Assert.Equal(eligible.PanelId, exact.PanelId);
        Assert.IsType<BrowserAgentIntent.Snapshot>(exact.Intent);

        var selectedProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserSnapshot,
            JsonSerializer.Serialize(new
            {
                panel_id = eligible.PanelId.Value,
            }));
        var broad = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(
                selectedProposal,
                [ineligible, eligible]));
        Assert.Equal(eligible.PanelId, broad.PanelId);
        Assert.IsType<BrowserAgentIntent.Snapshot>(broad.Intent);

        var exactWithPanelId =
            Assert.IsType<BrowserAgentIntentResult.Rejected>(
                BrowserAgentToolParser.Parse(
                    selectedProposal,
                    eligible));
        Assert.Equal(
            "invalid_tool_arguments",
            exactWithPanelId.StableCode);

        var ineligibleProposal = await ProposalAsync(
            BuiltInAgentTools.BrowserSnapshot,
            JsonSerializer.Serialize(new
            {
                panel_id = ineligible.PanelId.Value,
            }));
        var ineligibleSelection =
            Assert.IsType<BrowserAgentIntentResult.Rejected>(
                BrowserAgentToolParser.Parse(
                    ineligibleProposal,
                    [ineligible, eligible]));
        Assert.Equal(
            "invalid_tool_arguments",
            ineligibleSelection.StableCode);
    }

    [Fact]
    public void BroadSchemasEscapePanelIdsAsJsonValues()
    {
        var specialId = """panel-"]},"additionalProperties":true,"x":"\""";
        var maximumId = new string('p', 256);
        var special = ContextPanelWithId(
            "special",
            specialId,
            SessionCapabilities.BrowserReadState);
        var maximum = ContextPanelWithId(
            "maximum",
            maximumId,
            SessionCapabilities.BrowserReadState);

        var tools = BrowserAgentToolSet.For([special, maximum]);

        Assert.False(
            tools.Single()
                .InputSchema
                .GetProperty("additionalProperties")
                .GetBoolean());
        Assert.Equal(
            [specialId, maximumId],
            PanelIds(tools, BuiltInAgentTools.BrowserReadState));
        Assert.Throws<ArgumentException>(
            () => BrowserAgentToolSet.For(
                [.. Enumerable.Repeat(special, 257)]));
    }

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

    [Fact]
    public void LowLevelSchemasAreClosedAndExposeOnlyAtomicGestures()
    {
        var panel = ContextPanel(
            "low-level",
            SessionCapabilities.BrowserMouse,
            SessionCapabilities.BrowserKey,
            SessionCapabilities.BrowserScroll,
            SessionCapabilities.BrowserEvaluate,
            SessionCapabilities.BrowserOriginGuard,
            SessionCapabilities.BrowserAgentInputBarrier);

        var tools = BrowserAgentToolSet.For(panel);

        Assert.Equal(
            [
                BuiltInAgentTools.BrowserMouse,
                BuiltInAgentTools.BrowserKey,
                BuiltInAgentTools.BrowserScroll,
                BuiltInAgentTools.BrowserEvaluate,
            ],
            tools.Select(tool => tool.Name), StringComparer.Ordinal);
        Assert.All(tools, tool => Assert.False(
            tool.InputSchema.GetProperty("additionalProperties").GetBoolean()));
        var mouseActions = tools[0].InputSchema
            .GetProperty("properties").GetProperty("action")
            .GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString());
        Assert.Equal(["move", "click", "wheel"], mouseActions, StringComparer.Ordinal);
        var keyActions = tools[1].InputSchema
            .GetProperty("properties").GetProperty("action")
            .GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString());
        Assert.Equal(["press"], keyActions, StringComparer.Ordinal);
        Assert.Contains("side-effect-free", tools[3].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MouseParserReturnsExactBoundedInputAndFreshnessRevisions()
    {
        var proposal = await ProposalAsync(
            BuiltInAgentTools.BrowserMouse,
            """
            {
              "action":"click","x":12.5,"y":24.5,"button":"left",
              "buttons":["left"],"modifiers":["control","shift"],
              "click_count":2,"document_revision":7,"viewport_revision":8,
              "input_epoch":9
            }
            """);

        var parsed = Assert.IsType<BrowserAgentIntentResult.Parsed>(
            BrowserAgentToolParser.Parse(proposal));
        var mouse = Assert.IsType<BrowserAgentIntent.Mouse>(parsed.Intent);

        Assert.Equal(BrowserMouseAction.Click, mouse.Action);
        Assert.Equal(12.5, mouse.XCss);
        Assert.Equal(BrowserMouseButton.Left, mouse.Button);
        Assert.Equal(
            BrowserInputModifiers.Control | BrowserInputModifiers.Shift,
            mouse.Modifiers);
        Assert.Equal((7L, 8L, 9L),
            (mouse.DocumentRevision, mouse.ViewportRevision, mouse.InputEpoch));
    }

    [Theory]
    [InlineData(BuiltInAgentTools.BrowserMouse,
        "{\"action\":\"down\",\"x\":1,\"y\":1,\"button\":\"left\",\"click_count\":1,\"document_revision\":1,\"viewport_revision\":1,\"input_epoch\":1}")]
    [InlineData(BuiltInAgentTools.BrowserKey,
        "{\"action\":\"down\",\"key\":\"A\",\"document_revision\":1,\"viewport_revision\":1,\"input_epoch\":1}")]
    [InlineData(BuiltInAgentTools.BrowserScroll,
        "{\"origin_x\":1,\"origin_y\":1,\"delta_x\":0,\"delta_y\":0,\"document_revision\":1,\"viewport_revision\":1,\"input_epoch\":1}")]
    [InlineData(BuiltInAgentTools.BrowserEvaluate,
        "{\"source\":\"document.cookie\",\"world\":\"isolated\",\"document_revision\":1,\"viewport_revision\":1,\"input_epoch\":1}")]
    public async Task LowLevelParserRejectsUnsafeOrNonAtomicRequests(
        string toolName,
        string arguments)
    {
        var proposal = await ProposalAsync(toolName, arguments);

        var rejected = Assert.IsType<BrowserAgentIntentResult.Rejected>(
            BrowserAgentToolParser.Parse(proposal));

        Assert.Equal("invalid_tool_arguments", rejected.StableCode);
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
        var session = new NativeAgentSession(new AgentRunId("browser-contract"));
        return await session.RunTurnAsync(
            "Use the browser tool.",
            [Tool(name)],
            new ToolProvider(name, arguments),
            CancellationToken.None);
    }

    private static AgentToolDefinition Tool(string name) =>
        new(
            name,
            "Test browser tool.",
            """
            {
              "type": "object",
              "additionalProperties": true
            }
            """u8.ToArray());

    private static AgentContextPanel ContextPanel(
        string suffix,
        params string[] capabilities) =>
        ContextPanel(
            suffix,
            $"panel-{suffix}",
            PanelKind.Browser,
            SessionLifecycle.Active,
            capabilities);

    private static AgentContextPanel ContextPanel(
        string suffix,
        SessionLifecycle lifecycle,
        params string[] capabilities) =>
        ContextPanel(
            suffix,
            $"panel-{suffix}",
            PanelKind.Browser,
            lifecycle,
            capabilities);

    private static AgentContextPanel ContextPanel(
        string suffix,
        PanelKind kind,
        SessionLifecycle lifecycle,
        params string[] capabilities) =>
        ContextPanel(
            suffix,
            $"panel-{suffix}",
            kind,
            lifecycle,
            capabilities);

    private static AgentContextPanel ContextPanelWithId(
        string suffix,
        string panelIdValue,
        params string[] capabilities) =>
        ContextPanel(
            suffix,
            panelIdValue,
            PanelKind.Browser,
            SessionLifecycle.Active,
            capabilities);

    private static AgentContextPanel ContextPanel(
        string suffix,
        string panelIdValue,
        PanelKind kind,
        SessionLifecycle lifecycle,
        params string[] capabilities)
    {
        var sessionId = new SessionId($"session-{suffix}");
        var windowId = new WindowInstanceId($"window-{suffix}");
        var workspaceId = new WorkspaceInstanceId($"workspace-{suffix}");
        var tabId = new TabInstanceId($"tab-{suffix}");
        var panelId = new PanelInstanceId(panelIdValue);
        var panel = new PanelInstance(
            panelId,
            kind,
            $"Browser {suffix}",
            sessionId);
        var tab = new TabInstance(tabId, "Browsers", [panel], panelId);
        var graph = new WorkspaceGraphSnapshot(
            windowId,
            new WorkspaceInstance(
                workspaceId,
                "Research",
                [tab],
                tabId),
            revision: 2,
            lastSequence: 2);
        var descriptor = new SessionDescriptor(
            sessionId,
            kind,
            lifecycle,
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
                "browser-call",
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

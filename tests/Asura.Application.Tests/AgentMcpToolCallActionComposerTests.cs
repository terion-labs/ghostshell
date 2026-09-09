using System.Text.Json;
using Asura.Core;

namespace Asura.Application.Tests;

public sealed class AgentMcpToolCallActionComposerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Frozen_manifest_alias_is_bound_to_its_required_opaque_identity()
    {
        var first = Manifest();
        var same = Manifest();
        var changedRevision = Manifest(profileRevision: 8);
        var changedSchema = Manifest(
            schema: """{"type":"object","properties":{"path":{"type":"string"}}}""");
        var changedIdentity = Manifest(
            identityMaterial: "different opaque MCP tool identity");

        Assert.Equal(first.ManifestDigest, same.ManifestDigest);
        Assert.Equal(first.ProviderAlias, same.ProviderAlias);
        Assert.StartsWith("mcp_", first.ProviderAlias, StringComparison.Ordinal);
        Assert.Equal(
            AgentMcpToolManifest.ProviderAliasLength,
            first.ProviderAlias.Length);
        Assert.NotEqual(first.ProviderAlias, changedRevision.ProviderAlias, StringComparer.Ordinal);
        Assert.NotEqual(first.ProviderAlias, changedSchema.ProviderAlias, StringComparer.Ordinal);
        Assert.NotEqual(first.ProviderAlias, changedIdentity.ProviderAlias, StringComparer.Ordinal);
        Assert.DoesNotContain(first.ToolName, first.ProviderAlias, StringComparison.Ordinal);
    }

    [Fact]
    public void Frozen_manifest_rejects_a_missing_working_directory()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            Manifest(workingDirectory: null!));

        Assert.Equal("workingDirectory", exception.ParamName);
    }

    [Fact]
    public void Manifest_and_request_reject_ambiguous_or_oversized_json()
    {
        Assert.Throws<ArgumentException>(() => Manifest(
            schema: """{"type":"object","type":"array"}"""));
        Assert.Throws<ArgumentException>(() => new AgentMcpToolManifest(
            new McpServerProfileId("mcp.production"),
            7,
            "Production tools",
            "/opt/mcp/server",
            "/srv",
            "test-server",
            "1.2.3",
            "2025-11-25",
            "deploy",
            JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone(),
            default));
        Assert.Throws<ArgumentException>(() => Request(
            """{"value":1,"value":2}"""));
        Assert.Throws<ArgumentException>(() => Request(
            """{"outer":{"value":1,"value":2}}"""));
        Assert.Throws<ArgumentException>(() => Request(
            $$"""{"value":"{{new string('x', AgentMcpToolCallRequest.MaximumArgumentsBytes)}}"}"""));
        Assert.Throws<ArgumentException>(() => Request(
            """{"value":"sk-credential-canary"}"""));
        Assert.Throws<ArgumentException>(() => Request(
            """{"\u0074oken":"credential-canary"}"""));

        var nested = string.Concat(Enumerable.Repeat(
                """{"value":""",
                AgentMcpToolCallRequest.MaximumJsonDepth))
            + "0"
            + new string(
                '}',
                AgentMcpToolCallRequest.MaximumJsonDepth);
        Assert.Throws<ArgumentException>(() => Request(nested));
    }

    [Fact]
    public void MaximumBoundedFormatCharactersFitEscapedApprovalEnvelope()
    {
        var request = Request(
            $$"""{"value":"{{new string('\u00ad', 4_000)}}"}""");

        var action = new AgentMcpToolCallActionComposer().Prepare(
            Envelope(),
            Context(graphRevision: 11),
            request);

        Assert.Contains(
            "\\u00AD",
            action.Proposal.Presentation.Arguments[2].DisplayValue,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MaximumWorkingDirectoryFitsTheApprovalEnvelope()
    {
        var workingDirectory = "/"
            + new string(
                'w',
                AgentApprovalPresentation
                    .MaximumWorkingDirectoryBytes - 1);
        var request = new AgentMcpToolCallRequest(
            Manifest(workingDirectory: workingDirectory),
            JsonDocument.Parse("{}").RootElement.Clone());

        var action = new AgentMcpToolCallActionComposer().Prepare(
            Envelope(),
            Context(graphRevision: 11),
            request);

        Assert.Equal(
            workingDirectory,
            action.Proposal.Presentation.WorkingDirectory);
    }

    [Fact]
    public void Preparation_uses_the_generic_trusted_catalog_action_and_exact_display()
    {
        var request = Request(
            """{"path":"/srv/app","force":false}""");
        var action = new AgentMcpToolCallActionComposer().Prepare(
            Envelope(),
            Context(graphRevision: 11),
            request);

        Assert.Equal(BuiltInAgentTools.McpCall, action.Proposal.ToolName);
        Assert.Equal(Target(), action.Proposal.Target);
        Assert.Equal("MCP server: Production tools", action.Proposal.Presentation.TargetTitle);
        Assert.Equal("Local MCP stdio process", action.Proposal.Presentation.Host);
        Assert.Equal("/srv", action.Proposal.Presentation.WorkingDirectory);
        Assert.Collection(
            action.Proposal.Presentation.Arguments,
            argument => Assert.Equal(
                ("executable", "/opt/mcp/server"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("tool", "deploy"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("arguments", """{"path":"/srv/app","force":false}"""),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("profile_revision", "7"),
                (argument.Name, argument.DisplayValue)),
            argument => Assert.Equal(
                ("manifest", request.Manifest.ManifestDigest.Value),
                (argument.Name, argument.DisplayValue)));

        Assert.True(BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.McpCall,
            out var descriptor));
        Assert.Equal(AgentCapability.McpTools, descriptor!.Capability);
        Assert.Equal(AgentActionRisk.Mutation, descriptor.Risk);
    }

    [Fact]
    public void RemoteManifestPresentsExactEndpointWithoutProcessFields()
    {
        var manifest = new AgentMcpToolManifest(
            new McpServerProfileId("mcp.remote"),
            7,
            "Remote tools",
            McpServerTransportKind.StreamableHttp,
            "https://mcp.example.test/rpc",
            workingDirectory: null,
            "remote-server",
            "1.0.0",
            "2025-11-25",
            "deploy",
            JsonDocument.Parse("""{"type":"object"}""")
                .RootElement.Clone(),
            AgentActionDigest.FromUtf8("remote tool identity"));
        var request = new AgentMcpToolCallRequest(
            manifest,
            JsonDocument.Parse("{}").RootElement.Clone());

        var action = new AgentMcpToolCallActionComposer().Prepare(
            Envelope(),
            Context(graphRevision: 11),
            request);

        Assert.Equal(
            "Remote MCP Streamable HTTP server",
            action.Proposal.Presentation.Host);
        Assert.Null(action.Proposal.Presentation.WorkingDirectory);
        Assert.Equal(
            ("endpoint", "https://mcp.example.test/rpc"),
            (
                action.Proposal.Presentation.Arguments[0].Name,
                action.Proposal.Presentation.Arguments[0].DisplayValue));
    }

    [Fact]
    public void Fresh_binding_recomputes_target_evidence_and_rejects_manifest_drift()
    {
        var composer = new AgentMcpToolCallActionComposer();
        var request = Request("""{"path":"/srv/app"}""");
        var action = composer.Prepare(
            Envelope(),
            Context(graphRevision: 11),
            request);

        var binding = composer.BindForExecution(
            action,
            Context(graphRevision: 12),
            Manifest());

        Assert.NotEqual(
            action.Proposal.TargetFingerprint,
            binding.TargetFingerprint);
        Assert.Equal(
            action.Proposal.ArgumentDigest,
            binding.ArgumentDigest);
        Assert.Throws<InvalidOperationException>(() =>
            composer.BindForExecution(
                action,
                Context(graphRevision: 12),
                Manifest(profileRevision: 8)));
    }

    [Fact]
    public void Host_contract_exposes_only_typed_run_call_and_close_operations()
    {
        var methods = typeof(IAgentMcpSessionHost)
            .GetMethods()
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["CloseRunAsync", "OpenRunAsync", "RunToolAsync"],
            methods.Select(method => method.Name), StringComparer.Ordinal);
        Assert.All(
            methods,
            method => Assert.DoesNotContain(
                method.GetParameters(),
                parameter => parameter.ParameterType == typeof(object)));
        Assert.Empty(typeof(AgentMcpToolCallAction).GetConstructors());
    }

    private static AgentMcpToolCallRequest Request(string arguments) =>
        new(
            Manifest(),
            JsonDocument.Parse(arguments).RootElement.Clone());

    private static AgentMcpToolManifest Manifest(
        long profileRevision = 7,
        string schema = """{"type":"object","additionalProperties":false}""",
        string identityMaterial = "application MCP tool identity fixture",
        string workingDirectory = "/srv") =>
        new(
            new McpServerProfileId("mcp.production"),
            profileRevision,
            "Production tools",
            "/opt/mcp/server",
            workingDirectory,
            "test-server",
            "1.2.3",
            "2025-11-25",
            "deploy",
            JsonDocument.Parse(schema).RootElement.Clone(),
            AgentActionDigest.FromUtf8(
                identityMaterial));

    private static AgentActionEnvelope Envelope() =>
        new(
            new AgentActionId("mcp-action"),
            new AgentRunId("mcp-run"),
            new ActorDescriptor(
                new ActorId("mcp-agent"),
                ActorKind.Agent,
                "MCP agent"),
            policyGeneration: 3,
            Now,
            Now.AddMinutes(1));

    private static AgentContextSnapshot Context(long graphRevision)
    {
        var panel = new PanelInstance(
            Panel(),
            PanelKind.Terminal,
            "Production",
            Session());
        var tab = new TabInstance(
            Tab(),
            "Production",
            [panel],
            panel.Id);
        var graph = new WorkspaceGraphSnapshot(
            Window(),
            new WorkspaceInstance(
                Workspace(),
                "Production",
                [tab],
                tab.Id),
            graphRevision,
            graphRevision);
        var descriptor = new SessionDescriptor(
            Session(),
            PanelKind.Terminal,
            SessionLifecycle.Active,
            SessionHealth.Healthy,
            new SessionOwner(
                HostMode.Desktop,
                Window(),
                Workspace(),
                Tab(),
                Panel()),
            CapabilitySet.Empty,
            graphRevision,
            HasActiveWork: false,
            StatusDetail: "Ready");
        return new AgentContextSnapshot(
            Target(),
            [
                AgentContextPanel.ForGraphPanel(
                    graph,
                    Tab(),
                    Panel(),
                    descriptor),
            ],
            Now);
    }

    private static AgentTarget.Panel Target() =>
        new(Window(), Workspace(), Tab(), Panel());

    private static WindowInstanceId Window() => new("mcp-window");

    private static WorkspaceInstanceId Workspace() => new("mcp-workspace");

    private static TabInstanceId Tab() => new("mcp-tab");

    private static PanelInstanceId Panel() => new("mcp-panel");

    private static SessionId Session() => new("mcp-session");
}

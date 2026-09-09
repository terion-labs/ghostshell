using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class GitAgentToolSet
{
    private static readonly ToolSpec[] Specifications =
    [
        new(
            GitAgentToolNames.ReadState,
            SessionCapabilities.GitReadState,
            false,
            "Read bounded repository state from one exact hosted Git panel. Returns opaque state, change, branch, and remote references; never repository paths or remote URLs."),
        new(
            GitAgentToolNames.ReadDiff,
            SessionCapabilities.GitReadDiff,
            false,
            "Read one bounded secret-screened diff selected by exact opaque state and change references."),
        new(
            GitAgentToolNames.ReadRemoteRef,
            SessionCapabilities.GitReadRemoteRef,
            false,
            "Observe one exact configured remote branch and return a short-lived opaque remote-state reference."),
        new(GitAgentToolNames.Stage, SessionCapabilities.GitStage, true,
            "Stage one exact observed change. No path or stage-all input is accepted."),
        new(GitAgentToolNames.Unstage, SessionCapabilities.GitUnstage, true,
            "Unstage one exact observed change. No path or unstage-all input is accepted."),
        new(GitAgentToolNames.BranchCreate, SessionCapabilities.GitBranchCreate, true,
            "Create one local branch at the exact observed HEAD without switching to it."),
        new(GitAgentToolNames.BranchCheckout, SessionCapabilities.GitBranchCheckout, true,
            "Switch a clean repository to one exact observed local branch."),
        new(GitAgentToolNames.Commit, SessionCapabilities.GitCommit, true,
            "Commit exactly the observed staged index with hooks, editor, and signing disabled."),
        new(GitAgentToolNames.Push, SessionCapabilities.GitPush, true,
            "Publish one exact observed local branch with a fast-forward compare-and-swap remote lease."),
    ];

    public static ImmutableArray<AgentToolDefinition> For(AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return SupportsGitPanel(panel)
            ? [.. Specifications
                .Where(specification => Supports(panel, specification))
                .Select(specification => Tool(specification, panelIds: null))]
            : [];
    }

    public static ImmutableArray<AgentToolDefinition> For(
        IReadOnlyList<AgentContextPanel> panels)
    {
        var eligible = ActiveGitPanels(panels);
        var tools = ImmutableArray.CreateBuilder<AgentToolDefinition>();
        foreach (var specification in Specifications)
        {
            var panelIds = eligible
                .Where(panel => Supports(panel, specification))
                .Select(panel => panel.PanelId)
                .ToArray();
            if (panelIds.Length > 0)
            {
                tools.Add(Tool(specification, panelIds));
            }
        }

        return tools.ToImmutable();
    }

    public static ImmutableArray<AgentToolDefinition> ForWorkspace(
        IReadOnlyList<AgentContextPanel> panels)
    {
        var eligible = ActiveGitPanels(panels);
        return eligible.Length == 0
            ? []
            : For(eligible);
    }

    internal static ImmutableArray<AgentContextPanel> ActiveGitPanels(
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        if (panels.Count is < 1 or > AgentContextRequest.MaximumAllowedPanelCount
            || panels.Select(panel => panel.PanelId).Distinct().Count() != panels.Count)
        {
            throw new ArgumentException(
                "A Git tool scope requires a bounded unique panel collection.",
                nameof(panels));
        }

        return [.. panels.Where(SupportsGitPanel)];
    }

    internal static bool Supports(
        AgentContextPanel panel,
        string toolName)
    {
        var specification = Specifications.FirstOrDefault(value => string.Equals(
            value.Name,
            toolName,
            StringComparison.Ordinal));
        return specification is not null && Supports(panel, specification);
    }

    internal static string? RequiredCapability(string toolName) =>
        Specifications.FirstOrDefault(specification => string.Equals(
            specification.Name,
            toolName,
            StringComparison.Ordinal))?.Capability;

    private static bool Supports(AgentContextPanel panel, ToolSpec specification) =>
        SupportsGitPanel(panel)
        && panel.Capabilities.Contains(specification.Capability, StringComparer.Ordinal)
        && (!specification.IsMutation
            || panel.GitMetadata is { MutationsQuarantined: false });

    private static bool SupportsGitPanel(AgentContextPanel panel) =>
        OperatingSystem.IsMacOS()
        && panel.Kind == PanelKind.Git
        && panel.HasRegisteredGraph
        && panel.IsCurrentPanelSession
        && panel.SessionId is not null
        && panel.Lifecycle == SessionLifecycle.Active
        && panel.GitMetadata is
        {
            ConnectionKind: ConnectionKind.Local,
        };

    private static AgentToolDefinition Tool(
        ToolSpec specification,
        IReadOnlyList<PanelInstanceId>? panelIds)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        if (panelIds is not null)
        {
            WriteEnum(writer, "panel_id", panelIds.Select(static id => id.Value));
        }

        WriteArguments(writer, specification.Name);
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        if (panelIds is not null)
        {
            writer.WriteStringValue("panel_id");
        }

        foreach (var required in RequiredArguments(specification.Name))
        {
            writer.WriteStringValue(required);
        }

        writer.WriteEndArray();
        writer.WriteBoolean("additionalProperties", false);
        writer.WriteEndObject();
        writer.Flush();
        return new AgentToolDefinition(
            specification.Name,
            panelIds is null
                ? specification.Description
                : $"{specification.Description} Select the exact panel with panel_id.",
            buffer.WrittenSpan.ToArray());
    }

    private static void WriteArguments(Utf8JsonWriter writer, string toolName)
    {
        switch (toolName)
        {
            case GitAgentToolNames.ReadState:
                return;
            case GitAgentToolNames.ReadDiff:
                WriteReference(writer, "state_ref");
                WriteReference(writer, "change_ref");
                WriteEnum(writer, "area", ["staged", "unstaged"]);
                return;
            case GitAgentToolNames.ReadRemoteRef:
                WriteReference(writer, "state_ref");
                WriteReference(writer, "remote_ref");
                WriteReference(writer, "branch_ref");
                return;
            case GitAgentToolNames.Stage:
            case GitAgentToolNames.Unstage:
                WriteReference(writer, "state_ref");
                WriteReference(writer, "change_ref");
                return;
            case GitAgentToolNames.BranchCreate:
                WriteReference(writer, "state_ref");
                WriteString(writer, "new_branch_name", 1, 256);
                return;
            case GitAgentToolNames.BranchCheckout:
                WriteReference(writer, "state_ref");
                WriteReference(writer, "branch_ref");
                return;
            case GitAgentToolNames.Commit:
                WriteReference(writer, "state_ref");
                WriteString(writer, "subject", 1, 512);
                WriteString(writer, "body", 0, 32 * 1024);
                return;
            case GitAgentToolNames.Push:
                WriteReference(writer, "state_ref");
                WriteReference(writer, "remote_state_ref");
                WriteReference(writer, "remote_ref");
                WriteReference(writer, "branch_ref");
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(toolName));
        }
    }

    private static IReadOnlyList<string> RequiredArguments(string toolName) =>
        toolName switch
        {
            GitAgentToolNames.ReadState => [],
            GitAgentToolNames.ReadDiff => ["state_ref", "change_ref", "area"],
            GitAgentToolNames.ReadRemoteRef => ["state_ref", "remote_ref", "branch_ref"],
            GitAgentToolNames.Stage or GitAgentToolNames.Unstage => ["state_ref", "change_ref"],
            GitAgentToolNames.BranchCreate => ["state_ref", "new_branch_name"],
            GitAgentToolNames.BranchCheckout => ["state_ref", "branch_ref"],
            GitAgentToolNames.Commit => ["state_ref", "subject"],
            GitAgentToolNames.Push =>
                ["state_ref", "remote_state_ref", "remote_ref", "branch_ref"],
            _ => throw new ArgumentOutOfRangeException(nameof(toolName)),
        };

    private static void WriteReference(Utf8JsonWriter writer, string name) =>
        WriteString(writer, name, 1, 128);

    private static void WriteString(
        Utf8JsonWriter writer,
        string name,
        int minimumLength,
        int maximumLength)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteNumber("minLength", minimumLength);
        writer.WriteNumber("maxLength", maximumLength);
        writer.WriteEndObject();
    }

    private static void WriteEnum(
        Utf8JsonWriter writer,
        string name,
        IEnumerable<string> values)
    {
        writer.WriteStartObject(name);
        writer.WriteString("type", "string");
        writer.WriteStartArray("enum");
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private sealed record ToolSpec(
        string Name,
        string Capability,
        bool IsMutation,
        string Description);
}

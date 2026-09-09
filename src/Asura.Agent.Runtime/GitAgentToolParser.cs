using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;
using Asura.Git;

namespace Asura.Agent.Runtime;

internal static class GitAgentToolParser
{
    public static GitAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(panel);
        if (!GitAgentToolSet.Supports(panel, proposal.ToolName))
        {
            return UnavailableTool();
        }

        return TryReadProperties(proposal, out var properties, out var rejection)
            ? ParseRequest(proposal.ToolName, panel.PanelId, properties)
            : rejection;
    }

    public static GitAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (GitAgentToolSet.RequiredCapability(proposal.ToolName) is null)
        {
            return UnavailableTool();
        }

        if (!TryReadProperties(proposal, out var properties, out var rejection))
        {
            return rejection;
        }

        var eligible = GitAgentToolSet.ActiveGitPanels(panels)
            .Where(panel => GitAgentToolSet.Supports(panel, proposal.ToolName))
            .ToArray();
        if (!properties.Remove("panel_id", out var panelElement)
            || !TryGetString(panelElement, out var panelId))
        {
            return Invalid("A broad Git tool requires one exact panel_id.");
        }

        var selected = eligible.FirstOrDefault(panel => string.Equals(
            panel.PanelId.Value,
            panelId,
            StringComparison.Ordinal));
        return selected is null
            ? UnavailableTool()
            : ParseRequest(proposal.ToolName, selected.PanelId, properties);
    }

    private static GitAgentIntentResult ParseRequest(
        string toolName,
        PanelInstanceId panelId,
        Dictionary<string, JsonElement> properties)
    {
        try
        {
            AgentGitRequest request = toolName switch
            {
                GitAgentToolNames.ReadState => new AgentGitRequest.ReadState(panelId),
                GitAgentToolNames.ReadDiff => new AgentGitRequest.ReadDiff(
                    panelId,
                    State(properties),
                    new GitChangeReferenceId(RequiredString(properties, "change_ref")),
                    ReadArea(properties)),
                GitAgentToolNames.ReadRemoteRef => new AgentGitRequest.ReadRemoteRef(
                    panelId,
                    State(properties),
                    new GitRemoteReferenceId(RequiredString(properties, "remote_ref")),
                    new GitBranchReferenceId(RequiredString(properties, "branch_ref"))),
                GitAgentToolNames.Stage => new AgentGitRequest.Stage(
                    panelId,
                    State(properties),
                    new GitChangeReferenceId(RequiredString(properties, "change_ref"))),
                GitAgentToolNames.Unstage => new AgentGitRequest.Unstage(
                    panelId,
                    State(properties),
                    new GitChangeReferenceId(RequiredString(properties, "change_ref"))),
                GitAgentToolNames.BranchCreate => new AgentGitRequest.BranchCreate(
                    panelId,
                    State(properties),
                    RequiredString(properties, "new_branch_name")),
                GitAgentToolNames.BranchCheckout => new AgentGitRequest.BranchCheckout(
                    panelId,
                    State(properties),
                    new GitBranchReferenceId(RequiredString(properties, "branch_ref"))),
                GitAgentToolNames.Commit => new AgentGitRequest.Commit(
                    panelId,
                    State(properties),
                    RequiredString(properties, "subject"),
                    OptionalString(properties, "body")),
                GitAgentToolNames.Push => new AgentGitRequest.Push(
                    panelId,
                    State(properties),
                    new GitRemoteStateReferenceId(
                        RequiredString(properties, "remote_state_ref")),
                    new GitRemoteReferenceId(RequiredString(properties, "remote_ref")),
                    new GitBranchReferenceId(RequiredString(properties, "branch_ref"))),
                _ => throw new InvalidOperationException("Unknown Git tool."),
            };
            if (properties.Count != 0)
            {
                throw new ArgumentException("Git tool arguments contain unknown properties.");
            }

            return new GitAgentIntentResult.Parsed(panelId, request);
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or OverflowException)
        {
            return Invalid("Git tool arguments do not match the closed schema.");
        }
    }

    private static GitStateReferenceId State(Dictionary<string, JsonElement> properties) =>
        new(RequiredString(properties, "state_ref"));

    private static GitChangeArea ReadArea(Dictionary<string, JsonElement> properties) =>
        RequiredString(properties, "area") switch
        {
            "staged" => GitChangeArea.Staged,
            "unstaged" => GitChangeArea.Unstaged,
            _ => throw new ArgumentException("The Git diff area is invalid."),
        };

    private static bool TryReadProperties(
        AgentToolProposal proposal,
        out Dictionary<string, JsonElement> properties,
        out GitAgentIntentResult rejection)
    {
        properties = [];
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            rejection = Invalid("Git tool arguments must be one object.");
            return false;
        }

        foreach (var property in proposal.Arguments.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                rejection = Invalid("Git tool arguments contain a duplicate property.");
                return false;
            }
        }

        rejection = null!;
        return true;
    }

    private static string RequiredString(
        Dictionary<string, JsonElement> properties,
        string name)
    {
        if (!properties.Remove(name, out var element)
            || !TryGetString(element, out var value))
        {
            throw new ArgumentException($"Git tool argument '{name}' is required.");
        }

        return value;
    }

    private static string? OptionalString(
        Dictionary<string, JsonElement> properties,
        string name)
    {
        if (!properties.Remove(name, out var element))
        {
            return null;
        }

        if (!TryGetString(element, out var value))
        {
            throw new ArgumentException($"Git tool argument '{name}' must be a string.");
        }

        return value;
    }

    private static bool TryGetString(JsonElement element, out string value)
    {
        if (element.ValueKind == JsonValueKind.String
            && element.GetString() is { } text)
        {
            value = text;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static GitAgentIntentResult UnavailableTool() =>
        new GitAgentIntentResult.Rejected(
            "tool_not_available",
            "The requested Git tool is not available on this live panel.");

    private static GitAgentIntentResult Invalid(string message) =>
        new GitAgentIntentResult.Rejected("tool_arguments_invalid", message);
}

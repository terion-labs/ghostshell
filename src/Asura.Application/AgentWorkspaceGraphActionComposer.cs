using System.Globalization;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Binds read-only graph observations to the original run scope and to the
/// ordered, scope-clipped graph structure. Descriptive state can refresh;
/// membership, order, ownership, and panel kinds cannot.
/// </summary>
public sealed class AgentWorkspaceGraphActionComposer
{
    public AgentWorkspaceGraphAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentWorkspaceGraphRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var toolName = ToolName(request);
        var graph = ResolveClippedGraph(context);
        var proposal = new AgentActionProposal(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            toolName,
            context.Target,
            CreateStructuralFingerprint(context.Target, graph.Panels),
            CreateArgumentDigest(envelope.ActionId, toolName, request),
            CreatePresentation(context.Target, request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentWorkspaceGraphAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentWorkspaceGraphAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var toolName = ToolName(action.Request);
        var graph = ResolveClippedGraph(freshContext);
        var argumentDigest = CreateArgumentDigest(
            action.Proposal.Id,
            toolName,
            action.Request);
        if (!string.Equals(
                action.Proposal.ToolName,
                toolName,
                StringComparison.Ordinal)
            || action.Proposal.ArgumentDigest != argumentDigest)
        {
            throw new InvalidOperationException(
                "The prepared graph action no longer matches its typed request.");
        }

        var targetIdentity = AgentTargetIdentity.Create(freshContext.Target);
        if (action.Proposal.Target != freshContext.Target
            || action.Proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh graph target does not match the original run target.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            action.Proposal.Id,
            action.Proposal.RunId,
            action.Proposal.Actor.Id,
            toolName,
            freshContext.Target,
            targetIdentity,
            CreateStructuralFingerprint(freshContext.Target, graph.Panels),
            argumentDigest,
            action.Proposal.PolicyGeneration);
    }

    public AgentWorkspaceGraphActionResult Project(
        AgentWorkspaceGraphAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var graph = ResolveClippedGraph(freshContext);
        var scopeKind = ScopeKind(freshContext.Target);
        var scopeLimited = freshContext.Target is not AgentTarget.Workspace;
        var workspace = Workspace(graph.Panels[0]);
        AgentWorkspaceGraphActionResult result = action.Request switch
        {
            AgentWorkspaceGraphRequest.WorkspaceInspect =>
                new AgentWorkspaceGraphActionResult.WorkspaceInspected(
                    scopeKind,
                    scopeLimited,
                    new AgentWorkspaceGraphWorkspaceInspection(
                        workspace,
                        InspectTabs(graph.Panels))),
            AgentWorkspaceGraphRequest.TabList list =>
                new AgentWorkspaceGraphActionResult.TabsListed(
                    scopeKind,
                    scopeLimited,
                    Page(Tabs(graph.Panels), list.Offset)),
            AgentWorkspaceGraphRequest.PanelList list =>
                new AgentWorkspaceGraphActionResult.PanelsListed(
                    scopeKind,
                    scopeLimited,
                    Page(
                        graph.Panels.Select(Panel).ToArray(),
                        list.Offset)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action.Request.GetType(),
                "The graph request kind is unsupported."),
        };

        EnsureProjectionBound(result);
        return result;
    }

    private static ResolvedGraph ResolveClippedGraph(
        AgentContextSnapshot context)
    {
        if (context.Panels.Count
            > AgentTarget.SelectedPanels.MaximumPanelCount)
        {
            throw new ArgumentException(
                $"A governed graph scope cannot exceed {WorkspaceInstance.MaximumPanelCount} panels.",
                nameof(context));
        }

        var panels = context.Panels
            .OrderBy(panel => panel.GraphTabOrder)
            .ThenBy(panel => panel.GraphPanelOrder)
            .ToArray();
        if (panels.Any(panel =>
                !panel.HasRegisteredGraph
                || panel.GraphTabOrder is null
                || panel.GraphPanelOrder is null))
        {
            throw new ArgumentException(
                "A governed graph observation requires registered graph panels.",
                nameof(context));
        }

        var first = panels[0];
        if (panels.Any(panel =>
                panel.WindowId != first.WindowId
                || panel.WorkspaceId != first.WorkspaceId
                || panel.WorkspaceRevision != first.WorkspaceRevision
                || panel.GraphSequence != first.GraphSequence))
        {
            throw new ArgumentException(
                "A governed graph observation requires one consistent workspace snapshot.",
                nameof(context));
        }

        var duplicateOrder = panels
            .GroupBy(panel => (panel.GraphTabOrder, panel.GraphPanelOrder))
            .Any(group => group.Count() != 1);
        var conflictingTabTitles = panels
            .GroupBy(panel => panel.TabId)
            .Any(group => group
                .Select(panel => panel.TabTitle)
                .Distinct(StringComparer.Ordinal)
                .Count() != 1);
        if (duplicateOrder || conflictingTabTitles)
        {
            throw new ArgumentException(
                "The clipped graph contains inconsistent structural metadata.",
                nameof(context));
        }

        ValidateTarget(context.Target, panels);
        return new ResolvedGraph(panels);
    }

    private static void ValidateTarget(
        AgentTarget target,
        IReadOnlyList<AgentContextPanel> panels)
    {
        var first = panels[0];
        var ownerMatches = target switch
        {
            AgentTarget.Panel panel =>
                panels.Count == 1
                && panel.WindowId == first.WindowId
                && panel.WorkspaceId == first.WorkspaceId
                && panel.TabId == first.TabId
                && panel.PanelId == first.PanelId,
            AgentTarget.ConnectionSession session =>
                panels.Count == 1
                && first.SessionId == session.SessionId
                && first.IsCurrentPanelSession,
            AgentTarget.OpenTab tab =>
                tab.WindowId == first.WindowId
                && tab.WorkspaceId == first.WorkspaceId
                && panels.All(panel => panel.TabId == tab.TabId),
            AgentTarget.Workspace workspace =>
                workspace.WindowId == first.WindowId
                && workspace.WorkspaceId == first.WorkspaceId,
            AgentTarget.SelectedPanels selected =>
                SelectedTargetMatches(selected, panels),
            _ => false,
        };
        if (!ownerMatches)
        {
            throw new ArgumentException(
                "The resolved graph projection does not exactly match its run scope.",
                nameof(target));
        }
    }

    private static bool SelectedTargetMatches(
        AgentTarget.SelectedPanels target,
        IReadOnlyList<AgentContextPanel> panels)
    {
        if (target.Panels.Count != panels.Count)
        {
            return false;
        }

        var resolved = panels
            .Select(panel => new AgentTarget.Panel(
                panel.WindowId,
                panel.WorkspaceId,
                panel.TabId,
                panel.PanelId))
            .ToHashSet();
        return target.Panels.All(resolved.Contains);
    }

    private static IReadOnlyList<AgentWorkspaceGraphTabInspection> InspectTabs(
        IReadOnlyList<AgentContextPanel> panels) =>
        [.. panels
            .GroupBy(panel => panel.TabId)
            .Select(group => new AgentWorkspaceGraphTabInspection(
                Tab(group.First()),
                [.. group.Select(Panel)]))];

    private static IReadOnlyList<AgentWorkspaceGraphTab> Tabs(
        IReadOnlyList<AgentContextPanel> panels) =>
        [.. panels
            .GroupBy(panel => panel.TabId)
            .Select(group => Tab(group.First()))];

    private static AgentWorkspaceGraphWorkspace Workspace(
        AgentContextPanel panel) =>
        new(
            panel.WindowId,
            panel.WorkspaceId,
            panel.WorkspaceRevision,
            panel.GraphSequence,
            AgentWorkspaceGraphTitle.FromUntrusted(panel.WorkspaceTitle));

    private static AgentWorkspaceGraphTab Tab(AgentContextPanel panel) =>
        new(
            panel.WindowId,
            panel.WorkspaceId,
            panel.WorkspaceRevision,
            panel.GraphSequence,
            panel.TabId,
            panel.IsVisible,
            AgentWorkspaceGraphTitle.FromUntrusted(panel.TabTitle));

    private static AgentWorkspaceGraphPanel Panel(AgentContextPanel panel) =>
        new(
            panel.WindowId,
            panel.WorkspaceId,
            panel.WorkspaceRevision,
            panel.GraphSequence,
            panel.TabId,
            panel.PanelId,
            panel.Kind,
            panel.IsVisible,
            panel.IsFocused,
            AgentWorkspaceGraphTitle.FromUntrusted(panel.PanelTitle));

    private static AgentWorkspaceGraphPage<T> Page<T>(
        IReadOnlyList<T> values,
        int offset)
        where T : class
    {
        var items = values
            .Skip(offset)
            .Take(AgentWorkspaceGraphRequest.PageSize)
            .ToArray();
        var candidateNext = offset + items.Length;
        var nextOffset = candidateNext < values.Count
            && candidateNext <= AgentWorkspaceGraphRequest.MaximumOffset
                ? candidateNext
                : (int?)null;
        return new AgentWorkspaceGraphPage<T>(
            offset,
            items,
            nextOffset);
    }

    private static string ToolName(AgentWorkspaceGraphRequest request) =>
        request switch
        {
            AgentWorkspaceGraphRequest.WorkspaceInspect =>
                BuiltInAgentTools.WorkspaceInspect,
            AgentWorkspaceGraphRequest.TabList =>
                BuiltInAgentTools.TabList,
            AgentWorkspaceGraphRequest.PanelList =>
                BuiltInAgentTools.PanelList,
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The graph request kind is unsupported."),
        };

    private static AgentWorkspaceGraphScopeKind ScopeKind(AgentTarget target) =>
        target switch
        {
            AgentTarget.Panel => AgentWorkspaceGraphScopeKind.Panel,
            AgentTarget.ConnectionSession =>
                AgentWorkspaceGraphScopeKind.ConnectionSession,
            AgentTarget.OpenTab => AgentWorkspaceGraphScopeKind.OpenTab,
            AgentTarget.Workspace => AgentWorkspaceGraphScopeKind.Workspace,
            AgentTarget.SelectedPanels =>
                AgentWorkspaceGraphScopeKind.SelectedPanels,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The graph target kind is unsupported."),
        };

    private static AgentActionDigest CreateStructuralFingerprint(
        AgentTarget target,
        IReadOnlyList<AgentContextPanel> panels)
    {
        var builder = new StringBuilder("asura.agent-workspace-graph-structure|1");
        Append(builder, AgentTargetIdentity.Create(target).Value);
        Append(builder, panels.Count);
        foreach (var panel in panels)
        {
            Append(builder, panel.WindowId.Value);
            Append(builder, panel.WorkspaceId.Value);
            Append(builder, panel.TabId.Value);
            Append(builder, panel.PanelId.Value);
            Append(builder, (int)panel.Kind);
        }

        return AgentActionDigest.FromUtf8(builder.ToString());
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        string toolName,
        AgentWorkspaceGraphRequest request)
    {
        var builder = new StringBuilder("asura.agent-workspace-graph-action|1");
        Append(builder, actionId.Value);
        Append(builder, toolName);
        switch (request)
        {
            case AgentWorkspaceGraphRequest.WorkspaceInspect:
                Append(builder, "empty");
                break;
            case AgentWorkspaceGraphRequest.TabList tabs:
                Append(builder, tabs.Offset);
                break;
            case AgentWorkspaceGraphRequest.PanelList panels:
                Append(builder, panels.Offset);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.GetType(),
                    "The graph request kind is unsupported.");
        }

        return AgentActionDigest.FromUtf8(builder.ToString());
    }

    private static AgentApprovalPresentation CreatePresentation(
        AgentTarget target,
        AgentWorkspaceGraphRequest request)
    {
        var scope = target switch
        {
            AgentTarget.Panel panel =>
                $"Panel {panel.PanelId.Value}",
            AgentTarget.ConnectionSession session =>
                $"Connection session {session.SessionId.Value}",
            AgentTarget.OpenTab tab =>
                $"Tab {tab.TabId.Value}",
            AgentTarget.Workspace workspace =>
                $"Workspace {workspace.WorkspaceId.Value}",
            AgentTarget.SelectedPanels selected =>
                $"Selected panels in workspace {selected.Panels[0].WorkspaceId.Value}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The graph target kind is unsupported."),
        };
        var arguments = request switch
        {
            AgentWorkspaceGraphRequest.TabList tabs =>
                new[]
                {
                    new AgentApprovalArgument(
                        "offset",
                        tabs.Offset.ToString(CultureInfo.InvariantCulture)),
                },
            AgentWorkspaceGraphRequest.PanelList panels =>
                [
                    new AgentApprovalArgument(
                        "offset",
                        panels.Offset.ToString(CultureInfo.InvariantCulture)),
                ],
            _ => [],
        };
        return new AgentApprovalPresentation(
            scope,
            host: null,
            workingDirectory: null,
            arguments);
    }

    private static void EnsureProjectionBound(
        AgentWorkspaceGraphActionResult result)
    {
        var budget = new ProjectionBudget();
        budget.AddFixed(2 * 1024);
        switch (result)
        {
            case AgentWorkspaceGraphActionResult.WorkspaceInspected inspected:
                budget.Add(inspected.Workspace.Workspace);
                foreach (var tab in inspected.Workspace.Tabs)
                {
                    budget.Add(tab.Tab);
                    foreach (var panel in tab.Panels)
                    {
                        budget.Add(panel);
                    }
                }

                break;
            case AgentWorkspaceGraphActionResult.TabsListed listed:
                foreach (var tab in listed.Page.Items)
                {
                    budget.Add(tab);
                }

                break;
            case AgentWorkspaceGraphActionResult.PanelsListed listed:
                foreach (var panel in listed.Page.Items)
                {
                    budget.Add(panel);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.GetType(),
                    "The graph result kind is unsupported.");
        }

        if (budget.Bytes > AgentWorkspaceGraphActionResult.MaximumProjectionBytes)
        {
            throw new ArgumentException(
                "The governed graph projection exceeds 64 KiB.",
                nameof(result));
        }
    }

    private static void Append(
        StringBuilder builder,
        string value) =>
        builder
            .Append('|')
            .Append(Encoding.UTF8.GetByteCount(value)
                .ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);

    private static void Append(
        StringBuilder builder,
        int? value) =>
        Append(
            builder,
            value?.ToString(CultureInfo.InvariantCulture) ?? "null");

    private sealed record ResolvedGraph(
        IReadOnlyList<AgentContextPanel> Panels);

    private sealed class ProjectionBudget
    {
        public int Bytes { get; private set; }

        public void Add(AgentWorkspaceGraphWorkspace workspace)
        {
            AddFixed(384);
            AddText(workspace.WindowId.Value);
            AddText(workspace.WorkspaceId.Value);
            Add(workspace.Title);
        }

        public void Add(AgentWorkspaceGraphTab tab)
        {
            AddFixed(512);
            AddText(tab.WindowId.Value);
            AddText(tab.WorkspaceId.Value);
            AddText(tab.TabId.Value);
            Add(tab.Title);
        }

        public void Add(AgentWorkspaceGraphPanel panel)
        {
            AddFixed(640);
            AddText(panel.WindowId.Value);
            AddText(panel.WorkspaceId.Value);
            AddText(panel.TabId.Value);
            AddText(panel.PanelId.Value);
            Add(panel.Title);
        }

        public void AddFixed(int bytes) =>
            Bytes = checked(Bytes + bytes);

        private void Add(AgentWorkspaceGraphTitle? title)
        {
            AddFixed(96);
            if (title is not null)
            {
                AddText(title.Text);
            }
        }

        private void AddText(string value) =>
            // Six bytes per UTF-16 code unit is a conservative upper bound for
            // JSON escaping under the platform serializer.
            Bytes = checked(Bytes + value.Length * 6);
    }
}

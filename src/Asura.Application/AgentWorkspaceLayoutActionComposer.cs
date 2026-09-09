using System.Globalization;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Binds one layout mutation to the complete ordered graph of the run's single
/// workspace. Focus and descriptive metadata may change; topology may not.
/// </summary>
public sealed class AgentWorkspaceLayoutActionComposer
{
    public AgentWorkspaceLayoutAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentWorkspaceLayoutRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var panels = ResolveWorkspacePanels(context);
        ValidateRequestTarget(request, panels);
        var toolName = ToolName(request);
        var proposal = new AgentActionProposal(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            toolName,
            context.Target,
            CreateStructuralFingerprint(context.Target, panels),
            CreateArgumentDigest(envelope.ActionId, toolName, request),
            CreatePresentation(context, request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentWorkspaceLayoutAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentWorkspaceLayoutAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var panels = ResolveWorkspacePanels(freshContext);
        ValidateRequestTarget(action.Request, panels);
        var toolName = ToolName(action.Request);
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
                "The prepared layout action no longer matches its typed request.");
        }

        var targetIdentity = AgentTargetIdentity.Create(freshContext.Target);
        if (action.Proposal.Target != freshContext.Target
            || action.Proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh layout target does not match the run workspace.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            action.Proposal.Id,
            action.Proposal.RunId,
            action.Proposal.Actor.Id,
            toolName,
            freshContext.Target,
            targetIdentity,
            CreateStructuralFingerprint(freshContext.Target, panels),
            argumentDigest,
            action.Proposal.PolicyGeneration);
    }

    public static string ToolName(AgentWorkspaceLayoutRequest request) =>
        request switch
        {
            AgentWorkspaceLayoutRequest.ConnectionList => BuiltInAgentTools.ConnectionsList,
            AgentWorkspaceLayoutRequest.PanelConnect => BuiltInAgentTools.PanelConnect,
            AgentWorkspaceLayoutRequest.TabCreate => BuiltInAgentTools.TabCreate,
            AgentWorkspaceLayoutRequest.TabClose => BuiltInAgentTools.TabClose,
            AgentWorkspaceLayoutRequest.PanelAdd => BuiltInAgentTools.PanelAdd,
            AgentWorkspaceLayoutRequest.PanelSplit => BuiltInAgentTools.PanelSplit,
            AgentWorkspaceLayoutRequest.PanelClose => BuiltInAgentTools.PanelClose,
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The workspace layout request kind is unsupported."),
        };

    private static IReadOnlyList<AgentContextPanel> ResolveWorkspacePanels(
        AgentContextSnapshot context)
    {
        if (context.Target is not AgentTarget.Workspace workspaceTarget)
        {
            throw new ArgumentException(
                "Workspace layout tools require the run's complete workspace target.",
                nameof(context));
        }

        var panels = context.Panels
            .OrderBy(panel => panel.GraphTabOrder)
            .ThenBy(panel => panel.GraphPanelOrder)
            .ToArray();
        if (panels.Length == 0
            || panels.Any(panel =>
                !panel.HasRegisteredGraph
                || panel.GraphTabOrder is null
                || panel.GraphPanelOrder is null
                || panel.WindowId != workspaceTarget.WindowId
                || panel.WorkspaceId != workspaceTarget.WorkspaceId))
        {
            throw new ArgumentException(
                "Workspace layout tools require one complete registered graph.",
                nameof(context));
        }

        var first = panels[0];
        if (panels.Any(panel =>
                panel.WorkspaceRevision != first.WorkspaceRevision
                || panel.GraphSequence != first.GraphSequence)
            || panels
                .Select(panel => (panel.GraphTabOrder, panel.GraphPanelOrder))
                .Distinct()
                .Count() != panels.Length)
        {
            throw new ArgumentException(
                "Workspace layout tools require one consistent graph revision.",
                nameof(context));
        }

        return panels;
    }

    private static void ValidateRequestTarget(
        AgentWorkspaceLayoutRequest request,
        IReadOnlyList<AgentContextPanel> panels)
    {
        var valid = request switch
        {
            AgentWorkspaceLayoutRequest.ConnectionList => true,
            AgentWorkspaceLayoutRequest.PanelConnect connect =>
                panels.Any(panel => panel.PanelId == connect.PanelId),
            AgentWorkspaceLayoutRequest.TabCreate => true,
            AgentWorkspaceLayoutRequest.TabClose close =>
                panels.Any(panel => panel.TabId == close.TabId),
            AgentWorkspaceLayoutRequest.PanelAdd add =>
                panels.Any(panel => panel.TabId == add.TabId),
            AgentWorkspaceLayoutRequest.PanelSplit split =>
                panels.Any(panel => panel.PanelId == split.PanelId),
            AgentWorkspaceLayoutRequest.PanelClose close =>
                panels.Any(panel => panel.PanelId == close.PanelId),
            _ => false,
        };
        if (!valid)
        {
            throw new ArgumentException(
                "The selected layout target is outside the run workspace.",
                nameof(request));
        }
    }

    private static AgentActionDigest CreateStructuralFingerprint(
        AgentTarget target,
        IReadOnlyList<AgentContextPanel> panels)
    {
        var builder = new StringBuilder(
            "asura.agent-workspace-layout-structure|1");
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
        AgentWorkspaceLayoutRequest request)
    {
        var builder = new StringBuilder(
            "asura.agent-workspace-layout-action|1");
        Append(builder, actionId.Value);
        Append(builder, toolName);
        switch (request)
        {
            case AgentWorkspaceLayoutRequest.ConnectionList:
                break;
            case AgentWorkspaceLayoutRequest.PanelConnect connect:
                Append(builder, connect.PanelId.Value);
                Append(builder, connect.ConnectionRef);
                break;
            case AgentWorkspaceLayoutRequest.TabCreate create:
                Append(builder, (int)create.Kind);
                Append(builder, create.ConnectionRef ?? string.Empty);
                break;
            case AgentWorkspaceLayoutRequest.TabClose close:
                Append(builder, close.TabId.Value);
                break;
            case AgentWorkspaceLayoutRequest.PanelAdd add:
                Append(builder, add.TabId.Value);
                Append(builder, (int)add.Kind);
                Append(builder, add.ConnectionRef ?? string.Empty);
                break;
            case AgentWorkspaceLayoutRequest.PanelSplit split:
                Append(builder, split.PanelId.Value);
                Append(builder, (int)split.Orientation);
                Append(builder, (int)split.Kind);
                Append(builder, split.ConnectionRef ?? string.Empty);
                break;
            case AgentWorkspaceLayoutRequest.PanelClose close:
                Append(builder, close.PanelId.Value);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    request.GetType(),
                    "The workspace layout request kind is unsupported.");
        }

        return AgentActionDigest.FromUtf8(builder.ToString());
    }

    private static AgentApprovalPresentation CreatePresentation(
        AgentContextSnapshot context,
        AgentWorkspaceLayoutRequest request)
    {
        var workspace = (AgentTarget.Workspace)context.Target;
        AgentApprovalArgument[] arguments = request switch
        {
            AgentWorkspaceLayoutRequest.ConnectionList => [],
            AgentWorkspaceLayoutRequest.PanelConnect connect =>
            [
                Argument("panel_id", connect.PanelId.Value),
                Argument("connection_ref", connect.ConnectionRef),
            ],
            AgentWorkspaceLayoutRequest.TabCreate create =>
            [
                Argument("kind", PanelKindName(create.Kind)),
                .. ConnectionArguments(create.ConnectionRef),
            ],
            AgentWorkspaceLayoutRequest.TabClose close =>
            [
                Argument("tab_id", close.TabId.Value),
                Argument("effect", "Close this tab and its sessions"),
            ],
            AgentWorkspaceLayoutRequest.PanelAdd add =>
            [
                Argument("tab_id", add.TabId.Value),
                Argument("kind", PanelKindName(add.Kind)),
                .. ConnectionArguments(add.ConnectionRef),
            ],
            AgentWorkspaceLayoutRequest.PanelSplit split =>
            [
                Argument("panel_id", split.PanelId.Value),
                Argument("orientation", OrientationName(split.Orientation)),
                Argument("kind", PanelKindName(split.Kind)),
                .. ConnectionArguments(split.ConnectionRef),
            ],
            AgentWorkspaceLayoutRequest.PanelClose close =>
            [
                Argument("panel_id", close.PanelId.Value),
                Argument("effect", "Close this panel and its session"),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
        return new AgentApprovalPresentation(
            $"Workspace {workspace.WorkspaceId.Value}",
            host: null,
            workingDirectory: null,
            arguments);
    }

    private static AgentApprovalArgument Argument(string name, string value) =>
        new(name, value);

    private static AgentApprovalArgument[] ConnectionArguments(
        string? connectionRef) => connectionRef is null
            ? []
            : [Argument("connection_ref", connectionRef)];

    internal static string PanelKindName(PanelKind kind) => kind switch
    {
        PanelKind.Terminal => "terminal",
        PanelKind.Browser => "browser",
        PanelKind.FileViewer => "file_viewer",
        PanelKind.Statistics => "statistics",
        PanelKind.ProcessMonitor => "process_monitor",
        PanelKind.Placeholder => "placeholder",
        PanelKind.DatabaseViewer => "database_viewer",
        PanelKind.Docker => "docker",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    internal static string OrientationName(
        AgentPanelSplitOrientation orientation) => orientation switch
        {
            AgentPanelSplitOrientation.LeftRight => "left_right",
            AgentPanelSplitOrientation.TopBottom => "top_bottom",
            _ => throw new ArgumentOutOfRangeException(
                nameof(orientation),
                orientation,
                null),
        };

    private static void Append(StringBuilder builder, string value) =>
        builder
            .Append('|')
            .Append(Encoding.UTF8.GetByteCount(value)
                .ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value);

    private static void Append(StringBuilder builder, int value) =>
        Append(builder, value.ToString(CultureInfo.InvariantCulture));
}

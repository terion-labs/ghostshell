using System.Security.Cryptography;
using Asura.Core;

namespace Asura.Application;

public sealed partial class AgentDockerReadActionComposer
{
    public AgentDockerControlAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentDockerControlRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var resolved = ResolveControlForPreparation(context, request);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            request.ToolName,
            resolved.Context,
            CreateArgumentDigest(envelope.ActionId, request),
            CreateControlPresentation(resolved.Panel, request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentDockerControlAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentDockerControlAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);
        ValidatePreparedAction(action);
        var resolved = ResolveControlForExecution(freshContext, action.Request);
        if (action.Proposal.TargetIdentity
            != AgentTargetIdentity.Create(resolved.Context.Target))
        {
            throw new ArgumentException(
                "The fresh Docker target does not match the prepared mutation.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            action.Proposal.Id,
            action.Proposal.RunId,
            action.Proposal.Actor.Id,
            action.Request.ToolName,
            resolved.Context.Target,
            action.Proposal.TargetIdentity,
            resolved.Context.BindingFingerprint,
            action.Proposal.ArgumentDigest,
            action.Proposal.PolicyGeneration);
    }

    internal static void ValidatePreparedAction(AgentDockerControlAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(action.Request);
        ArgumentNullException.ThrowIfNull(action.Proposal);
        if (!string.Equals(
                action.Proposal.ToolName,
                action.Request.ToolName,
                StringComparison.Ordinal)
            || action.Proposal.ArgumentDigest
                != CreateArgumentDigest(action.Proposal.Id, action.Request))
        {
            throw new ArgumentException(
                "The prepared Docker mutation does not match its typed request.",
                nameof(action));
        }
    }

    private static ResolvedDockerContext ResolveControlForPreparation(
        AgentContextSnapshot context,
        AgentDockerControlRequest request)
    {
        var panel = RequireMatchingControlPanel(context, request);
        AgentTarget exactTarget = context.Target switch
        {
            AgentTarget.Panel panelTarget => ValidatePanelTarget(panelTarget, panel),
            AgentTarget.ConnectionSession sessionTarget =>
                ValidateSessionTarget(sessionTarget, panel),
            AgentTarget.OpenTab or AgentTarget.Workspace => ExactPanelTarget(panel),
            _ => throw new ArgumentException(
                "A Docker mutation requires a panel/session, tab, or workspace target.",
                nameof(context)),
        };
        if (!AgentTargetScope.Contains(context.Target, exactTarget))
        {
            throw new ArgumentException(
                "The Docker panel is outside the resolved run target.",
                nameof(context));
        }

        return new ResolvedDockerContext(
            new AgentContextSnapshot(exactTarget, [panel], context.CapturedAtUtc),
            panel);
    }

    private static ResolvedDockerContext ResolveControlForExecution(
        AgentContextSnapshot context,
        AgentDockerControlRequest request)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact Docker target must resolve to one panel.",
                nameof(context));
        }

        var panel = RequireMatchingControlPanel(context, request);
        _ = context.Target switch
        {
            AgentTarget.Panel panelTarget => ValidatePanelTarget(panelTarget, panel),
            AgentTarget.ConnectionSession sessionTarget =>
                ValidateSessionTarget(sessionTarget, panel),
            _ => throw new ArgumentException(
                "Docker execution requires a freshly resolved exact target.",
                nameof(context)),
        };
        return new ResolvedDockerContext(context, panel);
    }

    private static AgentContextPanel RequireMatchingControlPanel(
        AgentContextSnapshot context,
        AgentDockerControlRequest request)
    {
        var matches = context.Panels
            .Where(panel => panel.PanelId == request.PanelId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain one matching Docker panel.",
                nameof(context));
        }

        var panel = matches[0];
        if (panel.Kind != PanelKind.Docker
            || !panel.HasRegisteredGraph
            || !panel.IsCurrentPanelSession
            || panel.SessionId is null
            || panel.Lifecycle != SessionLifecycle.Active
            || !panel.Capabilities.Contains(
                request.RequiredSessionCapability,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "A Docker mutation requires one live local capable hosted session.",
                nameof(context));
        }

        return panel;
    }

    private static AgentApprovalPresentation CreateControlPresentation(
        AgentContextPanel panel,
        AgentDockerControlRequest request) => new(
            "Docker container mutation",
            panel.PanelTitle ?? "Docker",
            workingDirectory: null,
            [
                new AgentApprovalArgument("panel_id", panel.PanelId.Value),
                new AgentApprovalArgument("operation", request.ToolName),
                new AgentApprovalArgument("container_ref", request.Container.Value),
                new AgentApprovalArgument("expected_state", request.ExpectedState),
            ]);

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentDockerControlRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, actionId.Value);
        Append(hash, request.ToolName);
        Append(hash, request.PanelId.Value);
        Append(hash, request.Container.Value);
        Append(hash, request.EngineGeneration.Value);
        Append(hash, request.ContainerRevision.Value);
        Append(hash, (int)request.Action);
        Append(hash, request.ExpectedState);
        return new AgentActionDigest(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}

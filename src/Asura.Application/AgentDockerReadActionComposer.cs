using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Narrows a run scope to one exact hosted Docker panel and binds a closed
/// observation to authorization evidence. Provider output is projected in the
/// companion partial before it can reach the agent runtime.
/// </summary>
public sealed partial class AgentDockerReadActionComposer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentDockerReadAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentDockerReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var resolved = ResolveForPreparation(context, request);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            request.ToolName,
            resolved.Context,
            CreateArgumentDigest(envelope.ActionId, request),
            CreatePresentation(resolved.Panel, request),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentDockerReadAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentDockerReadAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);
        ValidatePreparedAction(action);
        var resolved = ResolveForExecution(freshContext, action.Request);
        if (action.Proposal.TargetIdentity
            != AgentTargetIdentity.Create(resolved.Context.Target))
        {
            throw new ArgumentException(
                "The fresh Docker target does not match the prepared action.",
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

    internal static void ValidatePreparedAction(AgentDockerReadAction action)
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
                "The prepared Docker action does not match its typed request.",
                nameof(action));
        }
    }

    private static ResolvedDockerContext ResolveForPreparation(
        AgentContextSnapshot context,
        AgentDockerReadRequest request)
    {
        var panel = RequireMatchingDockerPanel(context, request);
        AgentTarget exactTarget = context.Target switch
        {
            AgentTarget.Panel panelTarget => ValidatePanelTarget(panelTarget, panel),
            AgentTarget.ConnectionSession sessionTarget =>
                ValidateSessionTarget(sessionTarget, panel),
            AgentTarget.OpenTab or AgentTarget.Workspace => ExactPanelTarget(panel),
            _ => throw new ArgumentException(
                "A Docker observation requires a panel/session, tab, or workspace target.",
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

    private static ResolvedDockerContext ResolveForExecution(
        AgentContextSnapshot context,
        AgentDockerReadRequest request)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact Docker target must resolve to one panel.",
                nameof(context));
        }

        var panel = RequireMatchingDockerPanel(context, request);
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

    private static AgentContextPanel RequireMatchingDockerPanel(
        AgentContextSnapshot context,
        AgentDockerReadRequest request)
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
                "A Docker observation requires one live capable hosted Docker session.",
                nameof(context));
        }

        return panel;
    }

    private static AgentTarget ValidatePanelTarget(
        AgentTarget.Panel target,
        AgentContextPanel panel)
    {
        if (target != ExactPanelTarget(panel))
        {
            throw new ArgumentException(
                "The graph owner does not match the Docker panel target.",
                nameof(target));
        }

        return target;
    }

    private static AgentTarget ValidateSessionTarget(
        AgentTarget.ConnectionSession target,
        AgentContextPanel panel)
    {
        if (panel.SessionId is not { } sessionId || target.SessionId != sessionId)
        {
            throw new ArgumentException(
                "The graph owner does not match the Docker session target.",
                nameof(target));
        }

        return target;
    }

    private static AgentTarget.Panel ExactPanelTarget(AgentContextPanel panel) =>
        new(panel.WindowId, panel.WorkspaceId, panel.TabId, panel.PanelId);

    private static AgentApprovalPresentation CreatePresentation(
        AgentContextPanel panel,
        AgentDockerReadRequest request)
    {
        var arguments = new List<AgentApprovalArgument>
        {
            new("panel_id", panel.PanelId.Value),
            new("operation", request.ToolName),
        };
        switch (request)
        {
            case AgentDockerReadRequest.ReadState state:
                arguments.Add(new(
                    "maximum_resources_per_kind",
                    state.MaximumResourcesPerKind.ToString(CultureInfo.InvariantCulture)));
                break;
            case AgentDockerReadRequest.Inspect inspect:
                arguments.Add(new("resource_ref", inspect.Reference.Value));
                break;
            case AgentDockerReadRequest.Logs logs:
                arguments.Add(new("container_ref", logs.Container.Value));
                arguments.Add(new("limit", logs.Limit.ToString(CultureInfo.InvariantCulture)));
                arguments.Add(new(
                    "search_length",
                    (logs.SearchText?.Length ?? 0).ToString(CultureInfo.InvariantCulture)));
                break;
            case AgentDockerReadRequest.FilesList list:
                arguments.Add(new("resource_ref", list.Resource.Value));
                arguments.Add(new("path", list.Path));
                arguments.Add(new(
                    "maximum_entries",
                    list.MaximumEntries.ToString(CultureInfo.InvariantCulture)));
                break;
            case AgentDockerReadRequest.FilesStat stat:
                arguments.Add(new("resource_ref", stat.Resource.Value));
                arguments.Add(new("path", stat.Path));
                break;
            case AgentDockerReadRequest.FileRead read:
                arguments.Add(new("resource_ref", read.Resource.Value));
                arguments.Add(new("path", read.Path));
                arguments.Add(new(
                    "maximum_bytes",
                    read.MaximumBytes.ToString(CultureInfo.InvariantCulture)));
                break;
        }

        return new AgentApprovalPresentation(
            "Docker observation",
            panel.PanelTitle ?? "Docker",
            workingDirectory: null,
            arguments);
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentDockerReadRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, actionId.Value);
        Append(hash, request.ToolName);
        Append(hash, request.PanelId.Value);
        switch (request)
        {
            case AgentDockerReadRequest.ReadState state:
                Append(hash, state.MaximumResourcesPerKind);
                break;
            case AgentDockerReadRequest.Inspect inspect:
                Append(hash, inspect.Reference.Value);
                break;
            case AgentDockerReadRequest.Logs logs:
                Append(hash, logs.Container.Value);
                Append(hash, logs.Limit);
                Append(hash, logs.BeforeTimestamp);
                Append(hash, logs.SinceTimestamp);
                Append(hash, logs.SearchText);
                Append(hash, logs.ContextLines);
                break;
            case AgentDockerReadRequest.FilesList list:
                Append(hash, list.Resource.Value);
                Append(hash, list.Path);
                Append(hash, list.MaximumEntries);
                break;
            case AgentDockerReadRequest.FilesStat stat:
                Append(hash, stat.Resource.Value);
                Append(hash, stat.Path);
                break;
            case AgentDockerReadRequest.FileRead read:
                Append(hash, read.Resource.Value);
                Append(hash, read.Path);
                Append(hash, read.MaximumBytes);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }

        return new AgentActionDigest(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        if (value is null)
        {
            Append(hash, -1);
            return;
        }

        var bytes = StrictUtf8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private sealed record ResolvedDockerContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}

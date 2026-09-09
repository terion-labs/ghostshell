using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Narrows a run scope to one exact Statistics panel and binds one numeric
/// snapshot request to the authorization evidence consumed by the host.
/// </summary>
public sealed class AgentStatisticsReadActionComposer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentStatisticsReadAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentStatisticsReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var resolved = ResolveForPreparation(context, request.PanelId);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            BuiltInAgentTools.StatisticsRead,
            resolved.Context,
            CreateArgumentDigest(envelope.ActionId, request),
            new AgentApprovalPresentation(
                "Local System Statistics",
                "Local host",
                workingDirectory: null,
                [new AgentApprovalArgument(
                    "panel_id",
                    resolved.Panel.PanelId.Value)]),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentStatisticsReadAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentStatisticsReadAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var resolved = ResolveForExecution(
            freshContext,
            action.Request.PanelId);
        ValidatePreparedAction(action);
        var proposal = action.Proposal;
        var targetIdentity = AgentTargetIdentity.Create(resolved.Context.Target);
        if (proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh Statistics target does not match the prepared action.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            BuiltInAgentTools.StatisticsRead,
            resolved.Context.Target,
            targetIdentity,
            resolved.Context.BindingFingerprint,
            proposal.ArgumentDigest,
            proposal.PolicyGeneration);
    }

    /// <summary>
    /// The sole public construction seam for governed statistics results.
    /// Session output is validated as hostile even though it is numeric-only.
    /// </summary>
    public AgentStatisticsReadResult Project(
        AgentStatisticsReadAction action,
        SystemStatisticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidatePreparedAction(action);

        try
        {
            return new AgentStatisticsReadResult(
                snapshot.CapturedAtUtc,
                snapshot.HostUptime,
                snapshot.LogicalProcessorCount,
                snapshot.EnumeratedProcessCount,
                snapshot.ObservedProcessCount,
                snapshot.ObservedCpuPercent,
                snapshot.ObservedWorkingSetBytes,
                snapshot.NetworkReceivedBytesPerSecond,
                snapshot.NetworkSentBytesPerSecond);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "A Statistics panel capture contains invalid counters.",
                nameof(snapshot),
                exception);
        }
    }

    private static ResolvedStatisticsContext ResolveForPreparation(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        var panel = RequireMatchingStatisticsPanel(context, panelId);
        AgentTarget exactTarget;
        switch (context.Target)
        {
            case AgentTarget.Panel panelTarget:
                RequireSinglePanelContext(context);
                ValidatePanelTarget(panelTarget, panel);
                exactTarget = panelTarget;
                break;
            case AgentTarget.ConnectionSession sessionTarget:
                RequireSinglePanelContext(context);
                ValidateSessionTarget(sessionTarget, panel);
                exactTarget = sessionTarget;
                break;
            case AgentTarget.OpenTab:
            case AgentTarget.Workspace:
                var narrowedPanel = ExactPanelTarget(panel);
                if (!AgentTargetScope.Contains(context.Target, narrowedPanel))
                {
                    throw new ArgumentException(
                        "The Statistics panel is outside the resolved run target.",
                        nameof(context));
                }

                exactTarget = narrowedPanel;
                break;
            default:
                throw new ArgumentException(
                    "A statistics observation requires an exact panel/session, tab, or workspace target.",
                    nameof(context));
        }

        return new ResolvedStatisticsContext(
            new AgentContextSnapshot(
                exactTarget,
                [panel],
                context.CapturedAtUtc),
            panel);
    }

    private static ResolvedStatisticsContext ResolveForExecution(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        RequireSinglePanelContext(context);
        var panel = RequireMatchingStatisticsPanel(context, panelId);
        switch (context.Target)
        {
            case AgentTarget.Panel panelTarget:
                ValidatePanelTarget(panelTarget, panel);
                break;
            case AgentTarget.ConnectionSession sessionTarget:
                ValidateSessionTarget(sessionTarget, panel);
                break;
            default:
                throw new ArgumentException(
                    "Execution binding requires a freshly resolved exact Statistics target.",
                    nameof(context));
        }

        return new ResolvedStatisticsContext(context, panel);
    }

    private static AgentContextPanel RequireMatchingStatisticsPanel(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        var matches = context.Panels
            .Where(panel => panel.PanelId == panelId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain exactly one matching Statistics panel.",
                nameof(context));
        }

        var panel = matches[0];
        if (panel.Kind != PanelKind.Statistics)
        {
            throw new ArgumentException(
                "A statistics observation cannot target another panel kind.",
                nameof(context));
        }

        if (!panel.HasRegisteredGraph
            || !panel.IsCurrentPanelSession
            || panel.SessionId is null
            || panel.Lifecycle != SessionLifecycle.Active)
        {
            throw new ArgumentException(
                "A statistics observation requires one current active graph session.",
                nameof(context));
        }

        if (!panel.Capabilities.Contains(
                SessionCapabilities.StatisticsRead,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The Statistics session does not support statistics reading.",
                nameof(context));
        }

        return panel;
    }

    private static void RequireSinglePanelContext(AgentContextSnapshot context)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact Statistics target must resolve to one panel/session.",
                nameof(context));
        }
    }

    private static void ValidatePanelTarget(
        AgentTarget.Panel target,
        AgentContextPanel panel)
    {
        if (target != ExactPanelTarget(panel))
        {
            throw new ArgumentException(
                "The resolved graph owner does not match the exact Statistics panel target.",
                nameof(target));
        }
    }

    private static void ValidateSessionTarget(
        AgentTarget.ConnectionSession target,
        AgentContextPanel panel)
    {
        if (panel.SessionId is not { } sessionId
            || target.SessionId != sessionId)
        {
            throw new ArgumentException(
                "The resolved graph owner does not match the exact Statistics session target.",
                nameof(target));
        }
    }

    private static AgentTarget.Panel ExactPanelTarget(AgentContextPanel panel) =>
        new(
            panel.WindowId,
            panel.WorkspaceId,
            panel.TabId,
            panel.PanelId);

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        AgentStatisticsReadRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-statistics-read-action");
        AppendCanonical(hash, "1");
        AppendCanonical(hash, actionId.Value);
        AppendCanonical(hash, BuiltInAgentTools.StatisticsRead);
        AppendCanonical(hash, request.PanelId.Value);
        return new AgentActionDigest(
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void ValidatePreparedAction(AgentStatisticsReadAction action)
    {
        var digest = CreateArgumentDigest(
            action.Proposal.Id,
            action.Request);
        if (!string.Equals(
                action.Proposal.ToolName,
                BuiltInAgentTools.StatisticsRead,
                StringComparison.Ordinal)
            || action.Proposal.ArgumentDigest != digest)
        {
            throw new InvalidOperationException(
                "The prepared statistics action no longer matches its typed request.");
        }
    }

    private static void AppendCanonical(
        IncrementalHash hash,
        string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed record ResolvedStatisticsContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}

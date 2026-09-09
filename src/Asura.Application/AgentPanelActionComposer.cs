using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Narrows one resolved run scope to an exact live graph panel and binds the
/// typed request, approval presentation, and execution comparison evidence.
/// </summary>
public sealed class AgentPanelActionComposer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentPanelAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentPanelRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var prepared = Describe(request);
        var resolved = ResolveForPreparation(context, prepared.PanelId);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            prepared.ToolName,
            resolved.Context,
            CreateArgumentDigest(
                envelope.ActionId,
                prepared.ToolName,
                prepared.PanelId),
            CreatePresentation(resolved.Context.Target, resolved.Panel),
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentPanelAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentPanelAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var prepared = Describe(action.Request);
        var resolved = ResolveForExecution(freshContext, prepared.PanelId);
        var proposal = action.Proposal;
        var argumentDigest = CreateArgumentDigest(
            proposal.Id,
            prepared.ToolName,
            prepared.PanelId);
        if (!string.Equals(
                proposal.ToolName,
                prepared.ToolName,
                StringComparison.Ordinal)
            || proposal.ArgumentDigest != argumentDigest)
        {
            throw new InvalidOperationException(
                "The prepared panel action no longer matches its typed request.");
        }

        var targetIdentity = AgentTargetIdentity.Create(resolved.Context.Target);
        if (proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh panel target does not match the prepared action.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            prepared.ToolName,
            resolved.Context.Target,
            targetIdentity,
            resolved.Context.BindingFingerprint,
            argumentDigest,
            proposal.PolicyGeneration);
    }

    private static PreparedRequest Describe(AgentPanelRequest request) =>
        request switch
        {
            AgentPanelRequest.Inspect inspect =>
                new PreparedRequest(
                    BuiltInAgentTools.PanelInspect,
                    inspect.PanelId),
            AgentPanelRequest.Focus focus =>
                new PreparedRequest(
                    BuiltInAgentTools.PanelFocus,
                    focus.PanelId),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The agent panel request kind is unsupported."),
        };

    private static ResolvedPanelContext ResolveForPreparation(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        var panel = RequireMatchingPanel(context, panelId);
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
            default:
                var narrowedPanel = ExactPanelTarget(panel);
                if (!AgentTargetScope.Contains(context.Target, narrowedPanel))
                {
                    throw new ArgumentException(
                        "The selected panel is outside the resolved target.",
                        nameof(context));
                }

                exactTarget = narrowedPanel;
                break;
        }

        return new ResolvedPanelContext(
            new AgentContextSnapshot(
                exactTarget,
                [panel],
                context.CapturedAtUtc),
            panel);
    }

    private static ResolvedPanelContext ResolveForExecution(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        RequireSinglePanelContext(context);
        var panel = RequireMatchingPanel(context, panelId);
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
                    "Execution binding requires a freshly resolved exact panel target.",
                    nameof(context));
        }

        return new ResolvedPanelContext(context, panel);
    }

    private static AgentContextPanel RequireMatchingPanel(
        AgentContextSnapshot context,
        PanelInstanceId panelId)
    {
        var matches = context.Panels
            .Where(panel => panel.PanelId == panelId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain exactly one matching panel.",
                nameof(context));
        }

        var panel = matches[0];
        if (!panel.HasRegisteredGraph
            || !panel.IsCurrentPanelSession
            || panel.SessionId is null
            || panel.Lifecycle != SessionLifecycle.Active)
        {
            throw new ArgumentException(
                "A governed panel action requires one current live graph session.",
                nameof(context));
        }

        return panel;
    }

    private static void RequireSinglePanelContext(AgentContextSnapshot context)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact panel target must resolve to one panel/session.",
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
                "The resolved graph owner does not match the exact panel target.",
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
                "The resolved graph owner does not match the exact session target.",
                nameof(target));
        }
    }

    private static AgentTarget.Panel ExactPanelTarget(AgentContextPanel panel) =>
        new(
            panel.WindowId,
            panel.WorkspaceId,
            panel.TabId,
            panel.PanelId);

    private static AgentApprovalPresentation CreatePresentation(
        AgentTarget target,
        AgentContextPanel panel)
    {
        var targetTitle = target switch
        {
            AgentTarget.Panel exactPanel =>
                $"{EscapeForApproval(panel.PanelTitle ?? "Panel")} — panel "
                + $"{EscapeForApproval(exactPanel.PanelId.Value)} — session "
                + EscapeForApproval(panel.SessionId!.Value.Value),
            AgentTarget.ConnectionSession exactSession =>
                $"{EscapeForApproval(panel.PanelTitle ?? "Panel")} — session "
                + $"{EscapeForApproval(exactSession.SessionId.Value)} — panel "
                + EscapeForApproval(panel.PanelId.Value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The approval target kind is unsupported."),
        };
        return new AgentApprovalPresentation(
            targetTitle,
            panel.ConnectionBoundary is { } connection
                ? EscapeForApproval(connection)
                : "Workspace panel",
            panel.CurrentWorkingDirectory is { } currentDirectory
                ? EscapeForApproval(currentDirectory)
                : panel.InitialWorkingDirectory is { } initialDirectory
                    ? EscapeForApproval(initialDirectory)
                    : null,
            [
                new AgentApprovalArgument(
                    "panel_id",
                    EscapeForApproval(panel.PanelId.Value)),
            ]);
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        string toolName,
        PanelInstanceId panelId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-panel-action");
        AppendCanonical(hash, "1");
        AppendCanonical(hash, actionId.Value);
        AppendCanonical(hash, toolName);
        AppendCanonical(hash, panelId.Value);
        return new AgentActionDigest(
            Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendCanonical(
        IncrementalHash hash,
        string value)
    {
        var byteCount = GetStrictUtf8ByteCount(value, nameof(value));
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
        hash.AppendData(length);

        var bytes = StrictUtf8.GetBytes(value);
        try
        {
            hash.AppendData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static int GetStrictUtf8ByteCount(
        string value,
        string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Agent panel material must contain valid Unicode text.",
                parameterName,
                exception);
        }
    }

    private static string EscapeForApproval(string value)
    {
        _ = GetStrictUtf8ByteCount(value, nameof(value));
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\0':
                    builder.Append(@"\0");
                    break;
                case '\a':
                    builder.Append(@"\a");
                    break;
                case '\b':
                    builder.Append(@"\b");
                    break;
                case '\f':
                    builder.Append(@"\f");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\v':
                    builder.Append(@"\v");
                    break;
                default:
                    var category = char.GetUnicodeCategory(character);
                    if (char.IsControl(character)
                        || category is
                            UnicodeCategory.Format
                            or UnicodeCategory.LineSeparator
                            or UnicodeCategory.ParagraphSeparator)
                    {
                        builder.Append(@"\u");
                        builder.Append(
                            ((int)character).ToString(
                                "X4",
                                CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private sealed record PreparedRequest(
        string ToolName,
        PanelInstanceId PanelId);

    private sealed record ResolvedPanelContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}

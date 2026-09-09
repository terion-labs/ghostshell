using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Converts one typed terminal request into both its executable request/proposal pairing and
/// the exact human-readable material used for approval. The mapping is deliberately closed.
/// </summary>
public sealed class AgentTerminalActionComposer
{
    private const int MaximumMaterialValueBytes = 2 * 1024;
    private const double MaximumLogicalDimension = 1_000_000;
    private const double MaximumRenderScale = 100;
    private const int MinimumGridColumns = 2;
    private const int MaximumGridDimension = 1_000;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentTerminalAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentTerminalRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var prepared = Describe(request);
        var resolved = ResolveForPreparation(
            context,
            prepared.SessionId,
            prepared.Capability);
        RequireRequestSpecificCapabilities(request, resolved.Panel);
        var presentation = CreatePresentation(
            resolved.Context.Target,
            resolved.Panel,
            prepared.Arguments);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            prepared.ToolName,
            resolved.Context,
            CreateArgumentDigest(
                envelope.ActionId,
                prepared.ToolName,
                prepared.Arguments),
            presentation,
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentTerminalAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentTerminalAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var prepared = Describe(action.Request);
        var resolved = ResolveForExecution(
            freshContext,
            prepared.SessionId,
            prepared.Capability);
        RequireRequestSpecificCapabilities(action.Request, resolved.Panel);
        var proposal = action.Proposal;
        var argumentDigest = CreateArgumentDigest(
            proposal.Id,
            prepared.ToolName,
            prepared.Arguments);
        if (!string.Equals(
                proposal.ToolName,
                prepared.ToolName,
                StringComparison.Ordinal)
            || proposal.ArgumentDigest != argumentDigest)
        {
            throw new InvalidOperationException(
                "The prepared terminal action no longer matches its typed request.");
        }

        var targetIdentity = AgentTargetIdentity.Create(resolved.Context.Target);
        if (proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh terminal target does not match the prepared action.",
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

    // Request schema mapping. Each method materializes every execution-relevant field once;
    // both approval text and the authorization digest consume that same ordered field list.
    private static PreparedRequest Describe(AgentTerminalRequest request) =>
        request switch
        {
            AgentTerminalRequest.ReadScreen read => PrepareReadScreen(read),
            AgentTerminalRequest.ReadScreenDiff diff => PrepareReadScreenDiff(diff),
            AgentTerminalRequest.ReadScrollback read => PrepareReadScrollback(read),
            AgentTerminalRequest.FindScrollback find => PrepareFindScrollback(find),
            AgentTerminalRequest.FindOnScreen find => PrepareFindOnScreen(find),
            AgentTerminalRequest.FindRenderedHistory find =>
                PrepareFindRenderedHistory(find),
            AgentTerminalRequest.JumpToRenderedHistory jump =>
                PrepareJumpToRenderedHistory(jump),
            AgentTerminalRequest.ScrollViewport scroll => PrepareScrollViewport(scroll),
            AgentTerminalRequest.SendText write => PrepareSendText(write),
            AgentTerminalRequest.Paste paste => PreparePaste(paste),
            AgentTerminalRequest.SubmitText submit => PrepareSubmitText(submit),
            AgentTerminalRequest.SendKey key => PrepareSendKey(key),
            AgentTerminalRequest.SendChord chord => PrepareSendChord(chord),
            AgentTerminalRequest.SendMouse mouse => PrepareSendMouse(mouse),
            AgentTerminalRequest.WaitForDelay wait => PrepareWaitForDelay(wait),
            AgentTerminalRequest.WaitForText wait => PrepareWaitForText(wait),
            AgentTerminalRequest.WaitForChange wait => PrepareWaitForChange(wait),
            AgentTerminalRequest.WaitForStable wait => PrepareWaitForStable(wait),
            AgentTerminalRequest.WaitForPromptReady wait =>
                PrepareWaitForPromptReady(wait),
            AgentTerminalRequest.WaitForCommandFinished wait =>
                PrepareWaitForCommandFinished(wait),
            AgentTerminalRequest.Interrupt interrupt => PrepareInterrupt(interrupt),
            AgentTerminalRequest.Resize resize => PrepareResize(resize),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The agent terminal request kind is not supported."),
        };

    private static PreparedRequest PrepareReadScreen(
        AgentTerminalRequest.ReadScreen request)
    {
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalReadScreen,
            SessionCapabilities.TerminalReadScreen,
            request.SessionId,
            Argument("session_id", sessionId));
    }

    private static PreparedRequest PrepareReadScreenDiff(
        AgentTerminalRequest.ReadScreenDiff request)
    {
        var input = request.Input
            ?? throw new ArgumentException(
                "A rendered-screen diff requires bounded revision input.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalReadScreenDiff,
            SessionCapabilities.TerminalReadScreen,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("after_content_revision", Invariant(input.AfterContentRevision)),
            Argument("maximum_rows", Invariant(input.MaximumRowCount)));
    }

    private static PreparedRequest PrepareReadScrollback(
        AgentTerminalRequest.ReadScrollback request)
    {
        var input = request.Input
            ?? throw new ArgumentException(
                "A scrollback read requires bounded projection input.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        var arguments = new List<MaterialArgument>
        {
            Argument("session_id", sessionId),
            Argument("origin", input.Origin.ToString()),
            Argument("maximum_lines", Invariant(input.MaximumLines)),
        };
        if (input.RowAnchor is { } anchor)
        {
            arguments.Add(Argument(
                "anchor_content_revision",
                Invariant(anchor.ContentRevision)));
            arguments.Add(Argument("anchor_line_index", Invariant(anchor.LineIndex)));
        }

        return Prepared(
            BuiltInAgentTools.TerminalReadScrollback,
            SessionCapabilities.TerminalScrollbackRead,
            request.SessionId,
            [.. arguments]);
    }

    private static PreparedRequest PrepareFindScrollback(
        AgentTerminalRequest.FindScrollback request)
    {
        var input = request.Input
            ?? throw new ArgumentException(
                "A scrollback search requires bounded projection input.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalFind,
            SessionCapabilities.TerminalScrollbackFind,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("query", RequireMaterialText(input.Query, "terminal history query")),
            Argument("direction", input.Direction.ToString()),
            Argument("maximum_matches", Invariant(input.MaximumMatchCount)));
    }

    private static PreparedRequest PrepareFindOnScreen(
        AgentTerminalRequest.FindOnScreen request)
    {
        var input = request.Input
            ?? throw new ArgumentException(
                "A rendered-screen search requires bounded input.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalFindOnScreen,
            SessionCapabilities.TerminalReadScreen,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("query", RequireMaterialText(input.Query, "terminal screen query")),
            Argument("maximum_matches", Invariant(input.MaximumMatchCount)));
    }

    private static PreparedRequest PrepareFindRenderedHistory(
        AgentTerminalRequest.FindRenderedHistory request)
    {
        var input = request.Input
            ?? throw new ArgumentException(
                "A rendered terminal history search requires bounded input.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalFindRenderedHistory,
            SessionCapabilities.TerminalRenderedHistory,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("query", RequireMaterialText(input.Query, "rendered terminal history query")),
            Argument("direction", input.Direction.ToString()),
            Argument("maximum_matches", Invariant(input.MaximumMatchCount)));
    }

    private static PreparedRequest PrepareJumpToRenderedHistory(
        AgentTerminalRequest.JumpToRenderedHistory request)
    {
        var anchor = request.Anchor
            ?? throw new ArgumentException(
                "A rendered terminal history jump requires one row anchor.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalJumpToRenderedHistory,
            SessionCapabilities.TerminalRenderedHistory,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("anchor_content_revision", Invariant(anchor.ContentRevision)),
            Argument("anchor_row_index", Invariant(anchor.RowIndex)));
    }

    private static PreparedRequest PrepareScrollViewport(
        AgentTerminalRequest.ScrollViewport request)
    {
        var input = request.Input
            ?? throw new ArgumentException(
                "A viewport scroll requires bounded scroll input.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalScrollViewport,
            SessionCapabilities.TerminalScrollback,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("direction", input.Direction.ToString()),
            Argument("unit", input.Unit.ToString()),
            Argument("amount", Invariant(input.Amount)));
    }

    private static PreparedRequest PrepareSendText(
        AgentTerminalRequest.SendText request)
    {
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        var text = RequireMaterialText(request.Text, "terminal text");
        return Prepared(
            BuiltInAgentTools.TerminalSendText,
            SessionCapabilities.TerminalWrite,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("text", text));
    }

    private static PreparedRequest PreparePaste(
        AgentTerminalRequest.Paste request)
    {
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        var text = RequirePasteText(request.Text);
        return Prepared(
            BuiltInAgentTools.TerminalPaste,
            SessionCapabilities.TerminalPaste,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("text", text));
    }

    private static PreparedRequest PrepareSubmitText(
        AgentTerminalRequest.SubmitText request)
    {
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        var text = RequirePasteText(request.Text);
        return Prepared(
            BuiltInAgentTools.TerminalSubmitText,
            SessionCapabilities.TerminalPaste,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("text", text));
    }

    private static PreparedRequest PrepareSendKey(
        AgentTerminalRequest.SendKey request)
    {
        var keyStroke = request.KeyStroke
            ?? throw new ArgumentException(
                "A send-key action requires a terminal key stroke.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalSendKeys,
            SessionCapabilities.TerminalSendKeys,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("key", keyStroke.Key.ToString()),
            Argument("modifiers", keyStroke.Modifiers.ToString()),
            Argument("repeat_count", Invariant(keyStroke.RepeatCount)));
    }

    private static PreparedRequest PrepareSendChord(
        AgentTerminalRequest.SendChord request)
    {
        var chord = request.Chord
            ?? throw new ArgumentException(
                "A send-chord action requires a terminal character chord.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalSendChord,
            SessionCapabilities.TerminalSendChord,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("chord", FormatChord(chord)));
    }

    private static PreparedRequest PrepareSendMouse(
        AgentTerminalRequest.SendMouse request)
    {
        var mouseInput = request.MouseInput
            ?? throw new ArgumentException(
                "A send-mouse action requires a terminal mouse event.",
                nameof(request));
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        ArgumentOutOfRangeException.ThrowIfNegative(
            request.ExpectedContentRevision);
        return Prepared(
            BuiltInAgentTools.TerminalSendMouse,
            SessionCapabilities.TerminalMouse,
            request.SessionId,
            Argument("session_id", sessionId),
            Argument("button", mouseInput.Button.ToString()),
            Argument("kind", mouseInput.Kind.ToString()),
            Argument("column", Invariant(mouseInput.Column)),
            Argument("row", Invariant(mouseInput.Row)),
            Argument("modifiers", mouseInput.Modifiers.ToString()),
            Argument(
                "expected_content_revision",
                Invariant(request.ExpectedContentRevision)));
    }

    private static void RequireRequestSpecificCapabilities(
        AgentTerminalRequest request,
        AgentContextPanel panel)
    {
        if (request is AgentTerminalRequest.SendMouse
            && !panel.Capabilities.Contains(
                SessionCapabilities.TerminalRevisionBoundMouse,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"The terminal session does not support '{SessionCapabilities.TerminalRevisionBoundMouse}'.",
                nameof(panel));
        }


        if (request is AgentTerminalRequest.SubmitText
            && !panel.Capabilities.Contains(
                SessionCapabilities.TerminalEnter,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"The terminal session does not support '{SessionCapabilities.TerminalEnter}'.",
                nameof(panel));
        }
    }

    private static PreparedRequest PrepareWaitForDelay(
        AgentTerminalRequest.WaitForDelay request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A delay wait requires a terminal wait request.",
                nameof(request));
        var wait = value.Wait
            ?? throw new ArgumentException(
                "A delay wait requires bounded wait input.",
                nameof(request));
        var sessionId = RequireIdentifier(value.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalWait,
            SessionCapabilities.TerminalWait,
            value.SessionId,
            Argument("session_id", sessionId),
            Argument("delay", Invariant(wait.Delay)));
    }

    private static PreparedRequest PrepareWaitForText(
        AgentTerminalRequest.WaitForText request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A text-wait action requires a terminal wait request.",
                nameof(request));
        var wait = value.Wait
            ?? throw new ArgumentException(
                "A text-wait action requires bounded wait input.",
                nameof(request));
        var sessionId = RequireIdentifier(value.SessionId.Value, "session ID");
        var text = RequireMaterialText(wait.Text, "wait text");
        return Prepared(
            BuiltInAgentTools.TerminalWait,
            SessionCapabilities.TerminalWait,
            value.SessionId,
            Argument("session_id", sessionId),
            Argument("text", text),
            Argument("timeout", Invariant(wait.Timeout)));
    }

    private static PreparedRequest PrepareWaitForChange(
        AgentTerminalRequest.WaitForChange request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A change-wait action requires a terminal wait request.",
                nameof(request));
        var wait = value.Wait
            ?? throw new ArgumentException(
                "A change-wait action requires bounded wait input.",
                nameof(request));
        var sessionId = RequireIdentifier(value.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalWait,
            SessionCapabilities.TerminalWait,
            value.SessionId,
            Argument("session_id", sessionId),
            Argument("after_content_revision", Invariant(wait.AfterContentRevision)),
            Argument("timeout", Invariant(wait.Timeout)));
    }

    private static PreparedRequest PrepareWaitForStable(
        AgentTerminalRequest.WaitForStable request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A stability-wait action requires a terminal wait request.",
                nameof(request));
        var wait = value.Wait
            ?? throw new ArgumentException(
                "A stability-wait action requires bounded wait input.",
                nameof(request));
        var sessionId = RequireIdentifier(value.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalWait,
            SessionCapabilities.TerminalWait,
            value.SessionId,
            Argument("session_id", sessionId),
            Argument("stable_for", Invariant(wait.StableFor)),
            Argument("timeout", Invariant(wait.Timeout)));
    }

    private static PreparedRequest PrepareWaitForPromptReady(
        AgentTerminalRequest.WaitForPromptReady request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A prompt-ready wait requires a terminal wait request.",
                nameof(request));
        var wait = value.Wait
            ?? throw new ArgumentException(
                "A prompt-ready wait requires bounded shell-event input.",
                nameof(request));
        var sessionId = RequireIdentifier(value.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalWait,
            SessionCapabilities.TerminalWait,
            value.SessionId,
            Argument("session_id", sessionId),
            Argument("condition", "prompt_ready"),
            Argument(
                "after_shell_event_sequence",
                Invariant(wait.AfterShellEventSequence)),
            Argument("timeout", Invariant(wait.Timeout)));
    }

    private static PreparedRequest PrepareWaitForCommandFinished(
        AgentTerminalRequest.WaitForCommandFinished request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A command-finished wait requires a terminal wait request.",
                nameof(request));
        var wait = value.Wait
            ?? throw new ArgumentException(
                "A command-finished wait requires bounded shell-event input.",
                nameof(request));
        var sessionId = RequireIdentifier(value.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalWait,
            SessionCapabilities.TerminalWait,
            value.SessionId,
            Argument("session_id", sessionId),
            Argument("condition", "command_finished"),
            Argument(
                "after_shell_event_sequence",
                Invariant(wait.AfterShellEventSequence)),
            Argument("timeout", Invariant(wait.Timeout)));
    }

    private static PreparedRequest PrepareInterrupt(
        AgentTerminalRequest.Interrupt request)
    {
        var sessionId = RequireIdentifier(request.SessionId.Value, "session ID");
        return Prepared(
            BuiltInAgentTools.TerminalInterrupt,
            SessionCapabilities.TerminalInterrupt,
            request.SessionId,
            Argument("session_id", sessionId));
    }

    private static PreparedRequest PrepareResize(
        AgentTerminalRequest.Resize request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A resize action requires a terminal resize request.",
                nameof(request));
        var viewport = value.Viewport
            ?? throw new ArgumentException(
                "A resize action requires an exact viewport.",
                nameof(request));
        ValidateViewport(viewport);
        var sessionId = RequireIdentifier(value.SessionId.Value, "session ID");
        var attachmentId = RequireIdentifier(value.AttachmentId.Value, "attachment ID");
        return Prepared(
            BuiltInAgentTools.TerminalResize,
            SessionCapabilities.TerminalResize,
            value.SessionId,
            Argument("session_id", sessionId),
            Argument("attachment_id", attachmentId),
            Argument("logical_width", Invariant(viewport.LogicalWidth)),
            Argument("logical_height", Invariant(viewport.LogicalHeight)),
            Argument("render_scale", Invariant(viewport.RenderScale)),
            Argument("columns", Invariant(viewport.Columns)),
            Argument("rows", Invariant(viewport.Rows)));
    }

    // Live-target resolution. Preparation may narrow a trusted resolved scope by the request's
    // exact session ID; execution accepts only a freshly resolved exact target.
    private static ResolvedTerminalContext ResolveForPreparation(
        AgentContextSnapshot context,
        SessionId requestSessionId,
        string requiredCapability)
    {
        var panel = RequireMatchingTerminalPanel(
            context,
            requestSessionId,
            requiredCapability);
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
                ValidateSessionTarget(sessionTarget, panel, requestSessionId);
                exactTarget = sessionTarget;
                break;
            default:
                var narrowedPanel = ExactPanelTarget(panel);
                if (!panel.HasRegisteredGraph
                    || !panel.IsCurrentPanelSession
                    || !AgentTargetScope.Contains(context.Target, narrowedPanel))
                {
                    throw new ArgumentException(
                        "The matching terminal session is stale or outside the resolved target.",
                        nameof(context));
                }

                exactTarget = narrowedPanel;
                break;
        }

        var exactContext = new AgentContextSnapshot(
            exactTarget,
            [panel],
            context.CapturedAtUtc);
        return new ResolvedTerminalContext(exactContext, panel);
    }

    private static ResolvedTerminalContext ResolveForExecution(
        AgentContextSnapshot context,
        SessionId requestSessionId,
        string requiredCapability)
    {
        RequireSinglePanelContext(context);
        var panel = RequireMatchingTerminalPanel(
            context,
            requestSessionId,
            requiredCapability);
        switch (context.Target)
        {
            case AgentTarget.Panel panelTarget:
                ValidatePanelTarget(panelTarget, panel);
                break;
            case AgentTarget.ConnectionSession sessionTarget:
                ValidateSessionTarget(sessionTarget, panel, requestSessionId);
                break;
            default:
                throw new ArgumentException(
                    "Execution binding requires a freshly resolved exact terminal target.",
                    nameof(context));
        }

        return new ResolvedTerminalContext(context, panel);
    }

    private static AgentContextPanel RequireMatchingTerminalPanel(
        AgentContextSnapshot context,
        SessionId requestSessionId,
        string requiredCapability)
    {
        var matches = context.Panels
            .Where(panel => panel.SessionId == requestSessionId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain exactly one matching terminal session.",
                nameof(context));
        }

        var panel = matches[0];
        if (panel.Kind != PanelKind.Terminal)
        {
            throw new ArgumentException(
                "An agent terminal action cannot target a non-terminal panel.",
                nameof(context));
        }

        if (panel.Lifecycle != SessionLifecycle.Active)
        {
            throw new ArgumentException(
                "An agent terminal action requires an active terminal session.",
                nameof(context));
        }

        if (!panel.Capabilities.Contains(requiredCapability, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"The terminal session does not support '{requiredCapability}'.",
                nameof(context));
        }

        return panel;
    }

    private static void RequireSinglePanelContext(AgentContextSnapshot context)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact terminal target must resolve to one panel/session.",
                nameof(context));
        }
    }

    private static void ValidatePanelTarget(
        AgentTarget.Panel target,
        AgentContextPanel panel)
    {
        if (target.WindowId != panel.WindowId
            || target.WorkspaceId != panel.WorkspaceId
            || target.TabId != panel.TabId
            || target.PanelId != panel.PanelId
            || !panel.HasRegisteredGraph
            || !panel.IsCurrentPanelSession)
        {
            throw new ArgumentException(
                "The resolved terminal owner is stale or does not match the exact panel target.",
                nameof(target));
        }
    }

    private static void ValidateSessionTarget(
        AgentTarget.ConnectionSession target,
        AgentContextPanel panel,
        SessionId requestSessionId)
    {
        if (target.SessionId != requestSessionId
            || (panel.HasRegisteredGraph && !panel.IsCurrentPanelSession))
        {
            throw new ArgumentException(
                "The resolved terminal owner is stale or does not match the exact session target.",
                nameof(target));
        }
    }

    private static AgentTarget.Panel ExactPanelTarget(AgentContextPanel panel) =>
        new(
            panel.WindowId,
            panel.WorkspaceId,
            panel.TabId,
            panel.PanelId);

    // Approval and digest projection. The approval uses reversible escaping while the digest
    // hashes strict UTF-8 values directly with a versioned, length-prefixed schema.
    private static AgentApprovalPresentation CreatePresentation(
        AgentTarget target,
        AgentContextPanel panel,
        IReadOnlyList<MaterialArgument> arguments)
    {
        var targetTitle = target switch
        {
            AgentTarget.Panel exactPanel =>
                $"{EscapeForApproval(panel.PanelTitle ?? "Terminal")} — panel "
                + $"{EscapeForApproval(exactPanel.PanelId.Value)} — session "
                + EscapeForApproval(panel.SessionId!.Value.Value),
            AgentTarget.ConnectionSession exactSession =>
                $"{EscapeForApproval(panel.PanelTitle ?? "Terminal")} — session "
                + $"{EscapeForApproval(exactSession.SessionId.Value)} — panel "
                + EscapeForApproval(panel.PanelId.Value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The approval target kind is not supported."),
        };
        var approvalArguments = arguments
            .Select(argument => new AgentApprovalArgument(
                argument.Name,
                EscapeForApproval(argument.Value)))
            .ToArray();
        return new AgentApprovalPresentation(
            targetTitle,
            panel.ConnectionBoundary ?? "Local terminal",
            panel.CurrentWorkingDirectory
                ?? panel.InitialWorkingDirectory
                ?? "<not reported>",
            approvalArguments);
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        string toolName,
        IReadOnlyList<MaterialArgument> arguments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-terminal-action");
        AppendCanonical(hash, "1");
        AppendCanonical(hash, actionId.Value);
        AppendCanonical(hash, toolName);
        AppendCanonical(hash, Invariant(arguments.Count));
        foreach (var argument in arguments)
        {
            AppendCanonical(hash, argument.Name);
            AppendCanonical(hash, argument.Value);
        }

        return new AgentActionDigest(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendCanonical(IncrementalHash hash, string value)
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

    private static PreparedRequest Prepared(
        string toolName,
        string capability,
        SessionId sessionId,
        params MaterialArgument[] arguments) =>
        new(toolName, capability, sessionId, Array.AsReadOnly(arguments));

    private static MaterialArgument Argument(string name, string value)
    {
        _ = GetStrictUtf8ByteCount(name, nameof(name));
        _ = GetStrictUtf8ByteCount(value, nameof(value));
        return new MaterialArgument(name, value);
    }

    // Boundary text safety. Obvious credential-bearing terminal input is rejected until a
    // dedicated SecretRef execution path can resolve material inside the trusted adapter.
    private static string RequireIdentifier(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || GetStrictUtf8ByteCount(value, label) > 256)
        {
            throw new ArgumentException(
                $"The agent terminal {label} must be printable and bounded.",
                label);
        }

        return string.Concat(value);
    }

    private static string RequireMaterialText(
        string? value,
        string label)
    {
        if (value is null)
        {
            throw new ArgumentNullException(label);
        }

        if (GetStrictUtf8ByteCount(value, label) > MaximumMaterialValueBytes)
        {
            throw new ArgumentException(
                $"The {label} cannot be represented completely in a bounded approval.",
                label);
        }

        if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                $"The {label} appears to contain literal secret material; "
                + "terminal agent actions require a dedicated opaque secret reference path.",
                label);
        }

        return string.Concat(value);
    }

    private static string RequirePasteText(string? value)
    {
        var text = RequireMaterialText(value, "terminal paste");
        if (text.Length == 0
            || text.Any(character =>
                char.IsControl(character)
                && character is not ('\t' or '\r' or '\n')))
        {
            throw new ArgumentException(
                "Terminal paste must contain bounded text; only tabs and line breaks "
                + "may be control characters.",
                nameof(value));
        }

        return text;
    }

    private static int GetStrictUtf8ByteCount(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Agent terminal material must contain valid Unicode text.",
                parameterName,
                exception);
        }
    }

    // Reversible approval escaping prevents control and bidi-format characters from changing
    // what the human sees. Strict UTF-8 validation above makes surrogate handling total.
    private static string EscapeForApproval(string value)
    {
        _ = GetStrictUtf8ByteCount(value, nameof(value));
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
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
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\v':
                    builder.Append(@"\v");
                    break;
                case '\f':
                    builder.Append(@"\f");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                default:
                    if (char.IsHighSurrogate(character))
                    {
                        var rune = new Rune(character, value[++index]);
                        var runeCategory = Rune.GetUnicodeCategory(rune);
                        if (runeCategory is UnicodeCategory.Control
                            or UnicodeCategory.Format
                            or UnicodeCategory.LineSeparator
                            or UnicodeCategory.ParagraphSeparator)
                        {
                            builder
                                .Append(@"\U")
                                .Append(rune.Value.ToString(
                                    "X8",
                                    CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(rune);
                        }

                        break;
                    }

                    var category = char.GetUnicodeCategory(character);
                    if (char.IsControl(character)
                        || category is UnicodeCategory.Format
                            or UnicodeCategory.LineSeparator
                            or UnicodeCategory.ParagraphSeparator)
                    {
                        builder
                            .Append(@"\u")
                            .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
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

    // Canonical primitive formatting and resource bounds for the only request containing
    // floating-point and independently nullable dimensions.
    private static string Invariant(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "<automatic>";

    private static string Invariant(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static string Invariant(TimeSpan value) =>
        value.ToString("c", CultureInfo.InvariantCulture);

    private static string FormatChord(TerminalCharacterChord chord)
    {
        var modifier = chord.Modifier switch
        {
            TerminalCharacterChordModifier.Control => "Ctrl",
            TerminalCharacterChordModifier.Alt => "Alt",
            _ => throw new ArgumentOutOfRangeException(
                nameof(chord),
                chord.Modifier,
                "Unknown terminal character chord modifier."),
        };
        return string.Concat(
            modifier,
            "+",
            char.ToUpperInvariant(chord.Character));
    }

    private static void ValidateViewport(ViewportDescriptor viewport)
    {
        if (!double.IsFinite(viewport.LogicalWidth)
            || viewport.LogicalWidth < 0
            || viewport.LogicalWidth > MaximumLogicalDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport),
                viewport.LogicalWidth,
                "A terminal viewport width must be finite, non-negative, and bounded.");
        }

        if (!double.IsFinite(viewport.LogicalHeight)
            || viewport.LogicalHeight < 0
            || viewport.LogicalHeight > MaximumLogicalDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport),
                viewport.LogicalHeight,
                "A terminal viewport height must be finite, non-negative, and bounded.");
        }

        if (!double.IsFinite(viewport.RenderScale)
            || viewport.RenderScale <= 0
            || viewport.RenderScale > MaximumRenderScale)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport),
                viewport.RenderScale,
                "A terminal render scale must be finite, positive, and bounded.");
        }

        if (viewport.Columns is null or < MinimumGridColumns or > MaximumGridDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport),
                viewport.Columns,
                $"Terminal columns must be between {MinimumGridColumns} and "
                + $"{MaximumGridDimension}.");
        }

        if (viewport.Rows is null or < 1 or > MaximumGridDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewport),
                viewport.Rows,
                $"Terminal rows must be between 1 and {MaximumGridDimension}.");
        }
    }

    private sealed record MaterialArgument(string Name, string Value);

    private sealed record PreparedRequest(
        string ToolName,
        string Capability,
        SessionId SessionId,
        IReadOnlyList<MaterialArgument> Arguments);

    private sealed record ResolvedTerminalContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Converts one typed browser request into its executable request/proposal
/// pairing and the exact human-readable material used for approval.
/// </summary>
public sealed class AgentBrowserActionComposer
{
    public const int MaximumAgentAddressLength = 2_048;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public AgentBrowserAction Prepare(
        AgentActionEnvelope envelope,
        AgentContextSnapshot context,
        AgentBrowserRequest request)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);

        var prepared = Describe(request);
        var resolved = ResolveForPreparation(
            context,
            prepared.SessionId,
            prepared.Capability,
            prepared.RequiresOriginGuard);
        var arguments = BindInteractionDocument(
            request,
            resolved.Panel,
            prepared.Arguments,
            nameof(context));
        var presentation = CreatePresentation(
            resolved.Context.Target,
            resolved.Panel,
            arguments);
        var proposal = AgentActionProposal.FromContext(
            envelope.ActionId,
            envelope.RunId,
            envelope.Actor,
            prepared.ToolName,
            resolved.Context,
            CreateArgumentDigest(
                envelope.ActionId,
                prepared.ToolName,
                arguments),
            presentation,
            envelope.PolicyGeneration,
            envelope.CreatedAtUtc,
            envelope.DeadlineUtc);
        return new AgentBrowserAction(request, proposal);
    }

    public AgentActionExecutionBinding BindForExecution(
        AgentBrowserAction action,
        AgentContextSnapshot freshContext)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(freshContext);

        var prepared = Describe(action.Request);
        var resolved = ResolveForExecution(
            freshContext,
            prepared.SessionId,
            prepared.Capability,
            prepared.RequiresOriginGuard);
        var arguments = BindInteractionDocument(
            action.Request,
            resolved.Panel,
            prepared.Arguments,
            nameof(freshContext));
        var proposal = action.Proposal;
        var argumentDigest = CreateArgumentDigest(
            proposal.Id,
            prepared.ToolName,
            arguments);
        if (!string.Equals(
                proposal.ToolName,
                prepared.ToolName,
                StringComparison.Ordinal)
            || proposal.ArgumentDigest != argumentDigest)
        {
            throw new InvalidOperationException(
                "The prepared browser action no longer matches its typed request "
                + "or trusted interaction document.");
        }

        var targetIdentity = AgentTargetIdentity.Create(resolved.Context.Target);
        if (proposal.TargetIdentity != targetIdentity)
        {
            throw new ArgumentException(
                "The fresh browser target does not match the prepared action.",
                nameof(freshContext));
        }

        return new AgentActionExecutionBinding(
            proposal.Id,
            proposal.RunId,
            proposal.Actor.Id,
            prepared.ToolName,
            resolved.Context.Target,
            targetIdentity,
            // Browser load state, viewport, and input epochs are expected to
            // move between proposal authorization and dispatch. They are
            // validated by the typed request (when material) and again by the
            // session-host dispatch fence. Replacing the authorization
            // fingerprint with the latest descriptive snapshot would revoke
            // an otherwise identical one-action permit merely because the
            // renderer finished loading.
            proposal.TargetFingerprint,
            argumentDigest,
            proposal.PolicyGeneration);
    }

    private static PreparedRequest Describe(AgentBrowserRequest request) =>
        request switch
        {
            AgentBrowserRequest.ReadState read => PrepareReadState(read),
            AgentBrowserRequest.Snapshot snapshot => PrepareSnapshot(snapshot),
            AgentBrowserRequest.Wait wait => PrepareWait(wait),
            AgentBrowserRequest.Click click => PrepareClick(click),
            AgentBrowserRequest.Fill fill => PrepareFill(fill),
            AgentBrowserRequest.Check check => PrepareCheck(check),
            AgentBrowserRequest.Mouse mouse => PrepareMouse(mouse),
            AgentBrowserRequest.Key key => PrepareKey(key),
            AgentBrowserRequest.Scroll scroll => PrepareScroll(scroll),
            AgentBrowserRequest.Evaluate evaluate => PrepareEvaluate(evaluate),
            AgentBrowserRequest.Navigate navigate => PrepareNavigate(navigate),
            AgentBrowserRequest.Back back => PrepareBack(back),
            AgentBrowserRequest.Forward forward => PrepareForward(forward),
            AgentBrowserRequest.Reload reload => PrepareReload(reload),
            AgentBrowserRequest.Stop stop => PrepareStop(stop),
            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.GetType(),
                "The agent browser request kind is not supported."),
        };

    private static PreparedRequest PrepareReadState(
        AgentBrowserRequest.ReadState request) =>
        PrepareSessionOnly(
            BuiltInAgentTools.BrowserReadState,
            SessionCapabilities.BrowserReadState,
            request.SessionId);

    private static PreparedRequest PrepareSnapshot(
        AgentBrowserRequest.Snapshot request)
    {
        var query = request.Query ?? BrowserSnapshotQuery.Lean;
        var arguments = new List<MaterialArgument>
        {
            Argument(
                "session_id",
                RequireIdentifier(request.SessionId.Value, "session ID")),
            Argument(
                "interactive_only",
                query.InteractiveOnly ? "true" : "false"),
        };
        if (query.Filter is { } filter)
        {
            arguments.Add(Argument("filter", filter));
        }

        if (query.MaximumDepth is { } maximumDepth)
        {
            arguments.Add(Argument(
                "max_depth",
                maximumDepth.ToString(CultureInfo.InvariantCulture)));
        }

        return Prepared(
            BuiltInAgentTools.BrowserSnapshot,
            SessionCapabilities.BrowserSnapshot,
            request.SessionId,
            requiresOriginGuard: false,
            [.. arguments]);
    }

    private static PreparedRequest PrepareWait(
        AgentBrowserRequest.Wait request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A browser wait action requires a wait request.",
                nameof(request));
        var arguments = new List<MaterialArgument>
        {
            Argument(
                "session_id",
                RequireIdentifier(value.SessionId.Value, "session ID")),
            Argument("condition", WaitConditionName(value.Condition)),
            Argument(
                "timeout_ms",
                Milliseconds(value.Timeout)),
        };

        switch (value.Condition)
        {
            case BrowserWaitCondition.Delay delay:
                arguments.Add(Argument("delay_ms", Milliseconds(delay.Value)));
                break;
            case BrowserWaitCondition.LoadState loadState:
                arguments.Add(Argument(
                    "load_state",
                    loadState.Value.ToString().ToLowerInvariant()));
                break;
            case BrowserWaitCondition.UrlPattern pattern:
                arguments.Add(Argument("url_pattern", pattern.Value));
                break;
            case BrowserWaitCondition.Text text:
                arguments.Add(Argument("text", text.Value));
                break;
            case BrowserWaitCondition.ElementState element:
                arguments.Add(Argument(
                    "reference",
                    RequireIdentifier(
                        element.Reference.Value,
                        "element reference ID")));
                arguments.Add(Argument(
                    "document_revision",
                    element.SourceDocumentRevision.ToString(
                        CultureInfo.InvariantCulture)));
                arguments.Add(Argument(
                    "ref_state",
                    element.State.ToString().ToLowerInvariant()));
                arguments.Add(Argument(
                    "expected",
                    element.Expected ? "true" : "false"));
                break;
            case BrowserWaitCondition.DocumentRevision revision:
                arguments.Add(Argument(
                    "after_document_revision",
                    revision.After.ToString(CultureInfo.InvariantCulture)));
                break;
            case BrowserWaitCondition.NetworkIdle idle:
                arguments.Add(Argument(
                    "network_idle_ms",
                    Milliseconds(idle.QuietFor)));
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    value.Condition.GetType(),
                    "The browser wait condition is unsupported.");
        }

        return Prepared(
            BuiltInAgentTools.BrowserWait,
            SessionCapabilities.BrowserWait,
            value.SessionId,
            requiresOriginGuard: false,
            [.. arguments]);
    }

    private static PreparedRequest PrepareClick(
        AgentBrowserRequest.Click request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A browser click action requires a click request.",
                nameof(request));
        var sessionId = RequireIdentifier(
            value.SessionId.Value,
            "session ID");
        var referenceId = RequireIdentifier(
            value.Reference.Value,
            "element reference ID");

        return Prepared(
            BuiltInAgentTools.BrowserClick,
            SessionCapabilities.BrowserClick,
            value.SessionId,
            requiresOriginGuard: true,
            Argument("session_id", sessionId),
            Argument("reference", referenceId),
            Argument(
                "document_revision",
                value.DocumentRevision.ToString(CultureInfo.InvariantCulture)));
    }

    private static PreparedRequest PrepareFill(
        AgentBrowserRequest.Fill request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A browser fill action requires a fill request.",
                nameof(request));
        var sessionId = RequireIdentifier(
            value.SessionId.Value,
            "session ID");
        var referenceId = RequireIdentifier(
            value.Reference.Value,
            "element reference ID");
        var text = RequireFillText(value.Text);

        return Prepared(
            BuiltInAgentTools.BrowserFill,
            SessionCapabilities.BrowserFill,
            value.SessionId,
            requiresOriginGuard: true,
            Argument("session_id", sessionId),
            Argument("reference", referenceId),
            Argument(
                "document_revision",
                value.DocumentRevision.ToString(CultureInfo.InvariantCulture)),
            Argument(
                "text",
                text,
                QuoteForApproval(text)));
    }

    private static PreparedRequest PrepareCheck(
        AgentBrowserRequest.Check request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A browser check action requires a check request.",
                nameof(request));
        var sessionId = RequireIdentifier(
            value.SessionId.Value,
            "session ID");
        var referenceId = RequireIdentifier(
            value.Reference.Value,
            "element reference ID");

        return Prepared(
            BuiltInAgentTools.BrowserCheck,
            SessionCapabilities.BrowserCheck,
            value.SessionId,
            requiresOriginGuard: true,
            Argument("session_id", sessionId),
            Argument("reference", referenceId),
            Argument(
                "document_revision",
                value.DocumentRevision.ToString(CultureInfo.InvariantCulture)));
    }

    private static PreparedRequest PrepareMouse(AgentBrowserRequest.Mouse request)
    {
        var value = request.Value
            ?? throw new ArgumentException("A browser mouse action requires a request.", nameof(request));
        return Prepared(
            BuiltInAgentTools.BrowserMouse,
            SessionCapabilities.BrowserMouse,
            value.SessionId,
            requiresOriginGuard: true,
            [
                .. AutomationArguments(value.SessionId, value.Binding)
,
                Argument("action", value.Action.ToString().ToLowerInvariant()),
                Argument("x", Number(value.XCss)),
                Argument("y", Number(value.YCss)),
                Argument("button", value.Button.ToString().ToLowerInvariant()),
                Argument("buttons", MouseButtons(value.Buttons)),
                Argument("modifiers", Modifiers(value.Modifiers)),
                Argument("click_count", value.ClickCount.ToString(CultureInfo.InvariantCulture)),
                Argument("delta_x", Number(value.DeltaX)),
                Argument("delta_y", Number(value.DeltaY)),
            ]);
    }

    private static PreparedRequest PrepareKey(AgentBrowserRequest.Key request)
    {
        var value = request.Value
            ?? throw new ArgumentException("A browser key action requires a request.", nameof(request));
        return Prepared(
            BuiltInAgentTools.BrowserKey,
            SessionCapabilities.BrowserKey,
            value.SessionId,
            requiresOriginGuard: true,
            [
                .. AutomationArguments(value.SessionId, value.Binding)
,
                Argument("action", value.Action.ToString().ToLowerInvariant()),
                Argument("key", value.Key.ToString()),
                Argument("modifiers", Modifiers(value.Modifiers)),
            ]);
    }

    private static PreparedRequest PrepareScroll(AgentBrowserRequest.Scroll request)
    {
        var value = request.Value
            ?? throw new ArgumentException("A browser scroll action requires a request.", nameof(request));
        return Prepared(
            BuiltInAgentTools.BrowserScroll,
            SessionCapabilities.BrowserScroll,
            value.SessionId,
            requiresOriginGuard: true,
            [
                .. AutomationArguments(value.SessionId, value.Binding)
,
                Argument("origin_x", Number(value.OriginXCss)),
                Argument("origin_y", Number(value.OriginYCss)),
                Argument("delta_x", Number(value.DeltaX)),
                Argument("delta_y", Number(value.DeltaY)),
                Argument("modifiers", Modifiers(value.Modifiers)),
            ]);
    }

    private static PreparedRequest PrepareEvaluate(AgentBrowserRequest.Evaluate request)
    {
        var value = request.Value
            ?? throw new ArgumentException("A browser evaluate action requires a request.", nameof(request));
        return Prepared(
            BuiltInAgentTools.BrowserEvaluate,
            SessionCapabilities.BrowserEvaluate,
            value.SessionId,
            requiresOriginGuard: true,
            [
                .. AutomationArguments(value.SessionId, value.Binding)
,
                Argument("world", value.World.ToString().ToLowerInvariant()),
                Argument("await", value.AwaitPromise ? "true" : "false"),
                Argument("timeout_ms", Milliseconds(value.Timeout)),
                Argument("source", value.Source, QuoteForApproval(value.Source)),
            ]);
    }

    private static IEnumerable<MaterialArgument> AutomationArguments(
        SessionId sessionId,
        BrowserAutomationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return
        [
            Argument("session_id", RequireIdentifier(sessionId.Value, "session ID")),
            Argument("document_revision", binding.Document.DocumentRevision.ToString(CultureInfo.InvariantCulture)),
            Argument("viewport_revision", binding.ViewportRevision.ToString(CultureInfo.InvariantCulture)),
            Argument("input_epoch", binding.InputEpoch.ToString(CultureInfo.InvariantCulture)),
        ];
    }

    private static string Number(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static string Modifiers(BrowserInputModifiers modifiers) =>
        modifiers == BrowserInputModifiers.None
            ? "none"
            : string.Join(',', Enum.GetValues<BrowserInputModifiers>()
                .Where(value => value != BrowserInputModifiers.None && modifiers.HasFlag(value))
                .Select(value => value.ToString().ToLowerInvariant()));

    private static string MouseButtons(BrowserMouseButtons buttons) =>
        buttons == BrowserMouseButtons.None
            ? "none"
            : string.Join(',', Enum.GetValues<BrowserMouseButtons>()
                .Where(value => value != BrowserMouseButtons.None && buttons.HasFlag(value))
                .Select(value => value.ToString().ToLowerInvariant()));

    private static PreparedRequest PrepareNavigate(
        AgentBrowserRequest.Navigate request)
    {
        var value = request.Value
            ?? throw new ArgumentException(
                "A browser navigation action requires a navigation request.",
                nameof(request));
        var address = value.Address
            ?? throw new ArgumentException(
                "A browser navigation action requires an address.",
                nameof(request));
        var sessionId = RequireIdentifier(value.SessionId.Value, "session ID");
        var addressText = address.ToString();
        if (addressText.Length > MaximumAgentAddressLength)
        {
            throw new ArgumentException(
                $"An agent browser address cannot exceed {MaximumAgentAddressLength} characters.",
                nameof(request));
        }

        return Prepared(
            BuiltInAgentTools.BrowserNavigate,
            SessionCapabilities.BrowserNavigate,
            value.SessionId,
            requiresOriginGuard: true,
            Argument("session_id", sessionId),
            Argument("address", addressText));
    }

    private static PreparedRequest PrepareBack(
        AgentBrowserRequest.Back request) =>
        PrepareSessionOnly(
            BuiltInAgentTools.BrowserBack,
            SessionCapabilities.BrowserBack,
            request.SessionId,
            requiresOriginGuard: true);

    private static PreparedRequest PrepareForward(
        AgentBrowserRequest.Forward request) =>
        PrepareSessionOnly(
            BuiltInAgentTools.BrowserForward,
            SessionCapabilities.BrowserForward,
            request.SessionId,
            requiresOriginGuard: true);

    private static PreparedRequest PrepareReload(
        AgentBrowserRequest.Reload request) =>
        PrepareSessionOnly(
            BuiltInAgentTools.BrowserReload,
            SessionCapabilities.BrowserReload,
            request.SessionId,
            requiresOriginGuard: true);

    private static PreparedRequest PrepareStop(
        AgentBrowserRequest.Stop request) =>
        PrepareSessionOnly(
            BuiltInAgentTools.BrowserStop,
            SessionCapabilities.BrowserStop,
            request.SessionId);

    private static PreparedRequest PrepareSessionOnly(
        string toolName,
        string capability,
        SessionId sessionId,
        bool requiresOriginGuard = false) =>
        Prepared(
            toolName,
            capability,
            sessionId,
            requiresOriginGuard,
            Argument(
                "session_id",
                RequireIdentifier(sessionId.Value, "session ID")));

    // A broad run may select a panel, but panel_id is selection syntax rather
    // than operation material. The trusted runtime resolves that selection to
    // a session, and this composer binds the exact panel/session target.
    private static ResolvedBrowserContext ResolveForPreparation(
        AgentContextSnapshot context,
        SessionId requestSessionId,
        string requiredCapability,
        bool requiresOriginGuard)
    {
        var panel = RequireMatchingBrowserPanel(
            context,
            requestSessionId,
            requiredCapability,
            requiresOriginGuard);
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
                        "The matching browser session is stale or outside the resolved target.",
                        nameof(context));
                }

                exactTarget = narrowedPanel;
                break;
        }

        var exactContext = new AgentContextSnapshot(
            exactTarget,
            [panel],
            context.CapturedAtUtc);
        return new ResolvedBrowserContext(exactContext, panel);
    }

    private static ResolvedBrowserContext ResolveForExecution(
        AgentContextSnapshot context,
        SessionId requestSessionId,
        string requiredCapability,
        bool requiresOriginGuard)
    {
        RequireSinglePanelContext(context);
        var panel = RequireMatchingBrowserPanel(
            context,
            requestSessionId,
            requiredCapability,
            requiresOriginGuard);
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
                    "Execution binding requires a freshly resolved exact browser target.",
                    nameof(context));
        }

        return new ResolvedBrowserContext(context, panel);
    }

    private static AgentContextPanel RequireMatchingBrowserPanel(
        AgentContextSnapshot context,
        SessionId requestSessionId,
        string requiredCapability,
        bool requiresOriginGuard)
    {
        var matches = context.Panels
            .Where(panel => panel.SessionId == requestSessionId)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                "The resolved target must contain exactly one matching browser session.",
                nameof(context));
        }

        var panel = matches[0];
        if (panel.Kind != PanelKind.Browser)
        {
            throw new ArgumentException(
                "An agent browser action cannot target a non-browser panel.",
                nameof(context));
        }

        if (panel.Lifecycle != SessionLifecycle.Active)
        {
            throw new ArgumentException(
                "An agent browser action requires an active browser session.",
                nameof(context));
        }

        if (!panel.Capabilities.Contains(requiredCapability, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"The browser session does not support '{requiredCapability}'.",
                nameof(context));
        }

        if (requiresOriginGuard
            && !panel.Capabilities.Contains(
                SessionCapabilities.BrowserOriginGuard,
                StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "The browser session cannot contain governed navigation redirects.",
                nameof(context));
        }

        return panel;
    }

    private static IReadOnlyList<MaterialArgument> BindInteractionDocument(
        AgentBrowserRequest request,
        AgentContextPanel panel,
        IReadOnlyList<MaterialArgument> arguments,
        string contextParameterName)
    {
        var requestedRevision = request switch
        {
            AgentBrowserRequest.Click click =>
                click.Value.DocumentRevision,
            AgentBrowserRequest.Fill fill =>
                fill.Value.DocumentRevision,
            AgentBrowserRequest.Check check =>
                check.Value.DocumentRevision,
            AgentBrowserRequest.Wait
            {
                Value.Condition: BrowserWaitCondition.ElementState element,
            } => element.SourceDocumentRevision,
            _ => (long?)null,
        };
        var automationBinding = request switch
        {
            AgentBrowserRequest.Mouse mouse => mouse.Value.Binding,
            AgentBrowserRequest.Key key => key.Value.Binding,
            AgentBrowserRequest.Scroll scroll => scroll.Value.Binding,
            AgentBrowserRequest.Evaluate evaluate => evaluate.Value.Binding,
            _ => null,
        };
        if (automationBinding is not null)
        {
            if (panel.BrowserMetadata is not { Address: { } address } automationMetadata
                || automationMetadata.DocumentRevision
                    != automationBinding.Document.DocumentRevision
                || address != automationBinding.Document.Address
                || automationMetadata.Viewport != automationBinding.Viewport
                || automationMetadata.ViewportRevision != automationBinding.ViewportRevision
                || automationMetadata.InputEpoch != automationBinding.InputEpoch)
            {
                throw new ArgumentException(
                    "The browser document, viewport, or input epoch changed before dispatch.",
                    contextParameterName);
            }

            var automationBound = new MaterialArgument[arguments.Count + 1];
            automationBound[0] = arguments[0];
            automationBound[1] = Argument(
                "origin",
                automationMetadata.Origin.CanonicalValue);
            for (var index = 1; index < arguments.Count; index++)
            {
                automationBound[index + 1] = arguments[index];
            }

            return Array.AsReadOnly(automationBound);
        }

        if (requestedRevision is null)
        {
            return arguments;
        }

        if (panel.BrowserMetadata is not { } metadata
            || metadata.DocumentRevision != requestedRevision.Value)
        {
            throw new ArgumentException(
                "The browser interaction document changed after the element was observed.",
                contextParameterName);
        }

        var bound = new MaterialArgument[arguments.Count + 1];
        bound[0] = arguments[0];
        bound[1] = Argument("origin", metadata.Origin.CanonicalValue);
        for (var index = 1; index < arguments.Count; index++)
        {
            bound[index + 1] = arguments[index];
        }

        return Array.AsReadOnly(bound);
    }

    private static void RequireSinglePanelContext(AgentContextSnapshot context)
    {
        if (context.Panels.Count != 1)
        {
            throw new ArgumentException(
                "An exact browser target must resolve to one panel/session.",
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
                "The resolved browser owner is stale or does not match the exact panel target.",
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
                "The resolved browser owner is stale or does not match the exact session target.",
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
        AgentContextPanel panel,
        IReadOnlyList<MaterialArgument> arguments)
    {
        var targetTitle = target switch
        {
            AgentTarget.Panel exactPanel =>
                $"{EscapeForApproval(panel.PanelTitle ?? "Browser")} — panel "
                + $"{EscapeForApproval(exactPanel.PanelId.Value)} — session "
                + EscapeForApproval(panel.SessionId!.Value.Value),
            AgentTarget.ConnectionSession exactSession =>
                $"{EscapeForApproval(panel.PanelTitle ?? "Browser")} — session "
                + $"{EscapeForApproval(exactSession.SessionId.Value)} — panel "
                + EscapeForApproval(panel.PanelId.Value),
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.GetType(),
                "The approval target kind is not supported."),
        };
        var approvalArguments = arguments
            .Select(argument =>
                argument.ApprovalDisplayValue is { } displayValue
                    ? new AgentApprovalArgument(
                        argument.Name,
                        displayValue,
                        AgentApprovalArgument.MaximumEscapedValueBytes)
                    : new AgentApprovalArgument(
                        argument.Name,
                        EscapeForApproval(argument.Value)))
            .ToArray();
        return new AgentApprovalPresentation(
            targetTitle,
            "Embedded browser",
            workingDirectory: null,
            approvalArguments);
    }

    private static AgentActionDigest CreateArgumentDigest(
        AgentActionId actionId,
        string toolName,
        IReadOnlyList<MaterialArgument> arguments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendCanonical(hash, "asura.agent-browser-action");
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
        bool requiresOriginGuard = false,
        params MaterialArgument[] arguments) =>
        new(
            toolName,
            capability,
            sessionId,
            requiresOriginGuard,
            Array.AsReadOnly(arguments));

    private static MaterialArgument Argument(string name, string value)
    {
        _ = GetStrictUtf8ByteCount(name, nameof(name));
        _ = GetStrictUtf8ByteCount(value, nameof(value));
        return new MaterialArgument(
            name,
            value,
            ApprovalDisplayValue: null);
    }

    private static MaterialArgument Argument(
        string name,
        string value,
        string approvalDisplayValue)
    {
        _ = GetStrictUtf8ByteCount(name, nameof(name));
        _ = GetStrictUtf8ByteCount(value, nameof(value));
        _ = GetStrictUtf8ByteCount(
            approvalDisplayValue,
            nameof(approvalDisplayValue));
        return new MaterialArgument(
            name,
            value,
            string.Concat(approvalDisplayValue));
    }

    private static string RequireIdentifier(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || GetStrictUtf8ByteCount(value, label) > 256)
        {
            throw new ArgumentException(
                $"The agent browser {label} must be printable and bounded.",
                label);
        }

        return string.Concat(value);
    }

    private static string RequireFillText(string value)
    {
        if (AgentLiteralSecretValidator.ContainsLikelyLiteralSecret(value))
        {
            throw new ArgumentException(
                "The browser fill text appears to contain literal secret material; "
                + "browser agent actions require a dedicated opaque secret reference path.",
                nameof(value));
        }

        return string.Concat(value);
    }

    private static string WaitConditionName(BrowserWaitCondition condition) =>
        condition switch
        {
            BrowserWaitCondition.Delay => "delay",
            BrowserWaitCondition.LoadState => "load_state",
            BrowserWaitCondition.UrlPattern => "url_pattern",
            BrowserWaitCondition.Text => "text",
            BrowserWaitCondition.ElementState => "ref_state",
            BrowserWaitCondition.DocumentRevision => "document_revision",
            BrowserWaitCondition.NetworkIdle => "network_idle",
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

    private static string Milliseconds(TimeSpan value) =>
        checked((long)value.TotalMilliseconds).ToString(
            CultureInfo.InvariantCulture);

    private static int GetStrictUtf8ByteCount(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "Agent browser material must contain valid Unicode text.",
                parameterName,
                exception);
        }
    }

    // Reversible escaping keeps untrusted titles and material from changing
    // the meaning of the approval while retaining every address character.
    private static string QuoteForApproval(string value) =>
        string.Concat(
            '"',
            EscapeForApproval(value, escapeQuotes: true),
            '"');

    private static string EscapeForApproval(
        string value,
        bool escapeQuotes = false)
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
                case '"' when escapeQuotes:
                    builder.Append("\\\"");
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
                        builder.Append(character);
                        builder.Append(value[++index]);
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

    private static string Invariant(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private sealed record MaterialArgument(
        string Name,
        string Value,
        string? ApprovalDisplayValue);

    private sealed record PreparedRequest(
        string ToolName,
        string Capability,
        SessionId SessionId,
        bool RequiresOriginGuard,
        IReadOnlyList<MaterialArgument> Arguments);

    private sealed record ResolvedBrowserContext(
        AgentContextSnapshot Context,
        AgentContextPanel Panel);
}

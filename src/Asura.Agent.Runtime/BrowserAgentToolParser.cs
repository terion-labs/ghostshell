using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class BrowserAgentToolParser
{
    private const int MaximumUrlLength = 2_048;
    private const int MaximumFillTextBytes = 2 * 1_024;
    private const int MaximumWaitTextBytes = 2 * 1_024;
    private static readonly TimeSpan MaximumWait = TimeSpan.FromHours(1);

    public static BrowserAgentIntentResult Parse(AgentToolProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid("Tool arguments must be an object.");
        }

        if (!TryReadUniqueProperties(proposal.Arguments, out var properties))
        {
            return Invalid("Tool arguments cannot contain duplicate fields.");
        }

        return ParseProperties(proposal.ToolName, properties, panelId: null);
    }

    /// <summary>
    /// Parses a proposal for one exact browser panel. The provider cannot
    /// repeat or replace the host-owned panel identity in its arguments.
    /// </summary>
    public static BrowserAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(panel);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid("Tool arguments must be an object.");
        }

        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadUniqueProperties(proposal.Arguments, out var properties))
        {
            return Invalid("Tool arguments cannot contain duplicate fields.");
        }

        if (properties.ContainsKey("panel_id"))
        {
            return Invalid(
                "An exact browser tool does not accept a panel ID.");
        }

        return BrowserAgentToolSet.Supports(panel, proposal.ToolName)
            ? ParseProperties(
                proposal.ToolName,
                properties,
                panel.PanelId)
            : UnavailableTool();
    }

    /// <summary>
    /// Parses a proposal against a freshly resolved broad browser scope. Even
    /// a scope with one eligible browser requires its explicit panel ID so the
    /// schema shape cannot silently change with live capability availability.
    /// </summary>
    public static BrowserAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Arguments.ValueKind != JsonValueKind.Object)
        {
            return Invalid("Tool arguments must be an object.");
        }

        if (!IsKnownTool(proposal.ToolName))
        {
            return UnknownTool();
        }

        if (!TryReadUniqueProperties(proposal.Arguments, out var properties))
        {
            return Invalid("Tool arguments cannot contain duplicate fields.");
        }

        var activeBrowsers = BrowserAgentToolSet.ActiveBrowsers(panels);
        if (activeBrowsers.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.TryGetValue("panel_id", out var panelIdElement)
            || panelIdElement.ValueKind != JsonValueKind.String
            || panelIdElement.GetString() is not { } panelId)
        {
            return Invalid(
                "A multi-browser tool requires one exact panel_id.");
        }

        var selected = activeBrowsers.FirstOrDefault(
            panel => string.Equals(
                panel.PanelId.Value,
                panelId,
                StringComparison.Ordinal));
        if (selected is null
            || !BrowserAgentToolSet.Supports(
                selected,
                proposal.ToolName))
        {
            return Invalid(
                "The selected panel_id is not available for this browser tool.");
        }

        properties.Remove("panel_id");
        return ParseProperties(
            proposal.ToolName,
            properties,
            selected.PanelId);
    }

    private static BrowserAgentIntentResult ParseProperties(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId) =>
        toolName switch
        {
            BuiltInAgentTools.BrowserReadState =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.ReadState(),
                    panelId),
            BuiltInAgentTools.BrowserSnapshot =>
                ParseSnapshot(properties, panelId),
            BuiltInAgentTools.BrowserWait =>
                ParseWait(properties, panelId),
            BuiltInAgentTools.BrowserClick =>
                ParseClick(properties, panelId),
            BuiltInAgentTools.BrowserFill =>
                ParseFill(properties, panelId),
            BuiltInAgentTools.BrowserCheck =>
                ParseCheck(properties, panelId),
            BuiltInAgentTools.BrowserMouse =>
                ParseMouse(properties, panelId),
            BuiltInAgentTools.BrowserKey =>
                ParseKey(properties, panelId),
            BuiltInAgentTools.BrowserScroll =>
                ParseScroll(properties, panelId),
            BuiltInAgentTools.BrowserEvaluate =>
                ParseEvaluate(properties, panelId),
            BuiltInAgentTools.BrowserNavigate =>
                ParseNavigate(properties, panelId),
            BuiltInAgentTools.BrowserBack =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Back(),
                    panelId),
            BuiltInAgentTools.BrowserForward =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Forward(),
                    panelId),
            BuiltInAgentTools.BrowserReload =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Reload(),
                    panelId),
            BuiltInAgentTools.BrowserStop =>
                ParseEmpty(
                    properties,
                    new BrowserAgentIntent.Stop(),
                    panelId),
            _ => UnknownTool(),
        };

    private static BrowserAgentIntentResult ParseEmpty(
        IReadOnlyDictionary<string, JsonElement> properties,
        BrowserAgentIntent intent,
        PanelInstanceId? panelId) =>
        properties.Count == 0
            ? new BrowserAgentIntentResult.Parsed(intent, panelId)
            : Invalid("This tool does not accept arguments.");

    private static BrowserAgentIntentResult ParseSnapshot(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count > 3)
        {
            return Invalid("Browser snapshot received unsupported fields.");
        }

        var interactiveOnly = false;
        if (properties.TryGetValue("interactive_only", out var interactiveElement))
        {
            if (interactiveElement.ValueKind is not JsonValueKind.True
                and not JsonValueKind.False)
            {
                return Invalid("Browser snapshot interactive_only must be a boolean.");
            }

            interactiveOnly = interactiveElement.GetBoolean();
        }

        string? filter = null;
        if (properties.TryGetValue("filter", out var filterElement))
        {
            if (filterElement.ValueKind != JsonValueKind.String
                || filterElement.GetString() is not { } candidate)
            {
                return Invalid("Browser snapshot filter must be text.");
            }

            filter = candidate;
        }

        int? maximumDepth = null;
        if (properties.TryGetValue("max_depth", out var depthElement))
        {
            if (depthElement.ValueKind != JsonValueKind.Number
                || !depthElement.TryGetInt32(out var candidate))
            {
                return Invalid("Browser snapshot max_depth must be an integer.");
            }

            maximumDepth = candidate;
        }

        try
        {
            var query = new BrowserSnapshotQuery(
                interactiveOnly,
                filter,
                maximumDepth);
            return new BrowserAgentIntentResult.Parsed(
                new BrowserAgentIntent.Snapshot(
                    query.InteractiveOnly,
                    query.Filter,
                    query.MaximumDepth),
                panelId);
        }
        catch (ArgumentException)
        {
            return Invalid(
                "Browser snapshot requires a valid bounded filter and max_depth.");
        }
    }

    private static BrowserAgentIntentResult ParseNavigate(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue("url", out var urlElement)
            || urlElement.ValueKind != JsonValueKind.String
            || urlElement.GetString() is not { } url
            || url.Length is < 1 or > MaximumUrlLength
            || !string.Equals(url, url.Trim(), StringComparison.Ordinal)
            || !BrowserAddress.TryParse(url, out var address))
        {
            return Invalid(
                "Browser navigation requires one absolute HTTP(S) URL "
                + "or about:blank of at most 2048 characters.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Navigate(address),
            panelId);
    }

    private static BrowserAgentIntentResult ParseWait(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (!properties.TryGetValue("timeout_ms", out var timeoutElement)
            || timeoutElement.ValueKind != JsonValueKind.Number
            || !timeoutElement.TryGetInt32(out var timeoutMilliseconds)
            || timeoutMilliseconds < 1
            || timeoutMilliseconds > MaximumWait.TotalMilliseconds)
        {
            return Invalid(
                "Browser wait requires an explicit timeout up to one hour.");
        }

        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        BrowserWaitCondition? condition = null;
        if (properties.Count == 2
            && TryReadPositiveMilliseconds(
                properties,
                "delay_ms",
                timeoutMilliseconds,
                out var delay))
        {
            condition = new BrowserWaitCondition.Delay(delay);
        }
        else if (properties.Count == 2
            && properties.TryGetValue("load_state", out var loadStateElement)
            && loadStateElement.ValueKind == JsonValueKind.String
            && TryParseLoadState(loadStateElement.GetString(), out var loadState))
        {
            condition = new BrowserWaitCondition.LoadState(loadState);
        }
        else if (properties.Count == 2
            && properties.TryGetValue("url_pattern", out var patternElement)
            && TryGetString(patternElement, out var pattern)
            && IsAllowedUrlPattern(pattern))
        {
            condition = new BrowserWaitCondition.UrlPattern(pattern);
        }
        else if (properties.Count == 2
            && properties.TryGetValue("text", out var textElement)
            && TryGetString(textElement, out var text)
            && IsAllowedWaitText(text))
        {
            condition = new BrowserWaitCondition.Text(text);
        }
        else if (properties.Count == 5
            && TryReadElementBinding(
                properties,
                out var reference,
                out var documentRevision)
            && properties.TryGetValue("ref_state", out var stateElement)
            && stateElement.ValueKind == JsonValueKind.String
            && TryParseElementState(stateElement.GetString(), out var state)
            && properties.TryGetValue("expected", out var expectedElement)
            && expectedElement.ValueKind is JsonValueKind.True
                or JsonValueKind.False)
        {
            condition = new BrowserWaitCondition.ElementState(
                new BrowserElementReferenceId(reference),
                documentRevision,
                state,
                expectedElement.GetBoolean());
        }
        else if (properties.Count == 2
            && properties.TryGetValue(
                "after_document_revision",
                out var revisionElement)
            && revisionElement.ValueKind == JsonValueKind.Number
            && revisionElement.TryGetInt64(out var revision)
            && revision >= 0)
        {
            condition = new BrowserWaitCondition.DocumentRevision(revision);
        }
        else if (properties.Count == 2
            && TryReadPositiveMilliseconds(
                properties,
                "network_idle_ms",
                timeoutMilliseconds,
                out var quietFor))
        {
            condition = new BrowserWaitCondition.NetworkIdle(quietFor);
        }

        return condition is null
            ? Invalid(
                "Browser wait requires exactly one valid condition and no extra fields.")
            : new BrowserAgentIntentResult.Parsed(
                new BrowserAgentIntent.Wait(condition, timeout),
                panelId);
    }

    private static bool TryReadPositiveMilliseconds(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        int maximum,
        out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (!properties.TryGetValue(name, out var element)
            || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var milliseconds)
            || milliseconds < 1
            || milliseconds > maximum)
        {
            return false;
        }

        value = TimeSpan.FromMilliseconds(milliseconds);
        return true;
    }

    private static bool TryParseLoadState(
        string? value,
        out BrowserLoadState loadState)
    {
        loadState = value switch
        {
            "loading" => BrowserLoadState.Loading,
            "ready" => BrowserLoadState.Ready,
            "failed" => BrowserLoadState.Failed,
            _ => (BrowserLoadState)(-1),
        };
        return Enum.IsDefined(loadState);
    }

    private static bool TryParseElementState(
        string? value,
        out BrowserElementStateKind state)
    {
        state = value switch
        {
            "visible" => BrowserElementStateKind.Visible,
            "enabled" => BrowserElementStateKind.Enabled,
            "checked" => BrowserElementStateKind.Checked,
            "selected" => BrowserElementStateKind.Selected,
            "editable" => BrowserElementStateKind.Editable,
            "focused" => BrowserElementStateKind.Focused,
            _ => (BrowserElementStateKind)(-1),
        };
        return Enum.IsDefined(state);
    }

    private static bool IsAllowedUrlPattern(string pattern) =>
        IsAllowedWaitText(pattern)
        && Encoding.UTF8.GetByteCount(pattern) <= BrowserWaitRequest.MaximumUrlPatternBytes
        && (pattern.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith("about:", StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowedWaitText(string text) =>
        text.Length != 0
        && Encoding.UTF8.GetByteCount(text) <= MaximumWaitTextBytes
        && IsAllowedFillText(text)
        && !text.Contains('\0', StringComparison.Ordinal)
        && !text.Any(character =>
            char.IsControl(character)
                && character is not '\t' and not '\n' and not '\r');

    private static BrowserAgentIntentResult ParseClick(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !TryReadElementBinding(
                properties,
                out var reference,
                out var documentRevision))
        {
            return Invalid(
                "Browser click requires one URL-safe reference of at most "
                + "128 characters and one non-negative integer document_revision.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Click(
                new BrowserElementReferenceId(reference),
                documentRevision),
            panelId);
    }

    private static BrowserAgentIntentResult ParseFill(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 3
            || !TryReadElementBinding(
                properties,
                out var reference,
                out var documentRevision)
            || !properties.TryGetValue("text", out var textElement)
            || !TryGetString(textElement, out var text)
            || !IsAllowedFillText(text))
        {
            return Invalid(
                "Browser fill requires one URL-safe reference of at most "
                + "128 characters, one non-negative integer document_revision, "
                + "and well-formed text of at most 2048 UTF-8 bytes. Only tabs "
                + "and line breaks are allowed as control characters.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Fill(
                new BrowserElementReferenceId(reference),
                documentRevision,
                text),
            panelId);
    }

    private static BrowserAgentIntentResult ParseCheck(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !TryReadElementBinding(
                properties,
                out var reference,
                out var documentRevision))
        {
            return Invalid(
                "Browser check requires one URL-safe reference of at most "
                + "128 characters and one non-negative integer document_revision.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Check(
                new BrowserElementReferenceId(reference),
                documentRevision),
            panelId);
    }

    private static BrowserAgentIntentResult ParseMouse(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "action", "x", "y", "button", "buttons", "modifiers",
            "click_count", "delta_x", "delta_y", "document_revision",
            "viewport_revision", "input_epoch",
        };
        if (properties.Keys.Any(key => !allowed.Contains(key))
            || !TryReadAutomationRevisions(
                properties,
                out var documentRevision,
                out var viewportRevision,
                out var inputEpoch)
            || !TryReadFiniteNumber(properties, "x", out var x)
            || !TryReadFiniteNumber(properties, "y", out var y)
            || !TryReadEnum(properties, "action", ParseMouseAction, out BrowserMouseAction action)
            || !TryReadOptionalEnum(
                properties,
                "button",
                ParseMouseButton,
                BrowserMouseButton.None,
                out BrowserMouseButton button)
            || !TryReadOptionalModifiers(properties, out var modifiers)
            || !TryReadOptionalButtons(properties, out var buttons)
            || !TryReadOptionalInt(properties, "click_count", 0, out var clickCount)
            || !TryReadOptionalFiniteNumber(properties, "delta_x", 0, out var deltaX)
            || !TryReadOptionalFiniteNumber(properties, "delta_y", 0, out var deltaY))
        {
            return Invalid("Browser mouse input has invalid or extra fields.");
        }

        try
        {
            // A temporary maximum viewport validates action-dependent bounds;
            // the trusted runtime replaces it with the exact observed viewport.
            var binding = ParserBinding(documentRevision, viewportRevision, inputEpoch);
            _ = new BrowserMouseRequest(
                new SessionId("parser"), binding, action, x, y, button,
                buttons, modifiers, clickCount, deltaX, deltaY);
        }
        catch (ArgumentException)
        {
            return Invalid("Browser mouse input has inconsistent action fields or out-of-range values.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Mouse(
                action, x, y, button, buttons, modifiers, clickCount,
                deltaX, deltaY, documentRevision, viewportRevision, inputEpoch),
            panelId);
    }

    private static BrowserAgentIntentResult ParseKey(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Keys.Any(key => key is not (
                "action" or "key" or "modifiers" or "document_revision"
                or "viewport_revision" or "input_epoch"))
            || !TryReadAutomationRevisions(
                properties,
                out var documentRevision,
                out var viewportRevision,
                out var inputEpoch)
            || !TryReadEnum(properties, "action", ParseKeyAction, out BrowserKeyAction action)
            || !TryReadEnum(properties, "key", ParseKey, out BrowserKey key)
            || !TryReadOptionalModifiers(properties, out var modifiers))
        {
            return Invalid("Browser key input requires one normalized key/action and exact revisions.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Key(
                action, key, modifiers, documentRevision, viewportRevision, inputEpoch),
            panelId);
    }

    private static BrowserAgentIntentResult ParseScroll(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Keys.Any(key => key is not (
                "origin_x" or "origin_y" or "delta_x" or "delta_y"
                or "modifiers" or "document_revision" or "viewport_revision"
                or "input_epoch"))
            || !TryReadAutomationRevisions(
                properties,
                out var documentRevision,
                out var viewportRevision,
                out var inputEpoch)
            || !TryReadFiniteNumber(properties, "origin_x", out var originX)
            || !TryReadFiniteNumber(properties, "origin_y", out var originY)
            || !TryReadFiniteNumber(properties, "delta_x", out var deltaX)
            || !TryReadFiniteNumber(properties, "delta_y", out var deltaY)
            || !TryReadOptionalModifiers(properties, out var modifiers))
        {
            return Invalid("Browser scroll requires a bounded CSS origin, deltas, and exact revisions.");
        }

        try
        {
            _ = new BrowserScrollRequest(
                new SessionId("parser"),
                ParserBinding(documentRevision, viewportRevision, inputEpoch),
                originX, originY, deltaX, deltaY, modifiers);
        }
        catch (ArgumentException)
        {
            return Invalid("Browser scroll origin or deltas are out of range.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Scroll(
                originX, originY, deltaX, deltaY, modifiers,
                documentRevision, viewportRevision, inputEpoch),
            panelId);
    }

    private static BrowserAgentIntentResult ParseEvaluate(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Keys.Any(key => key is not (
                "source" or "world" or "await" or "timeout_ms"
                or "document_revision" or "viewport_revision" or "input_epoch"))
            || !TryReadAutomationRevisions(
                properties,
                out var documentRevision,
                out var viewportRevision,
                out var inputEpoch)
            || !properties.TryGetValue("source", out var sourceElement)
            || !TryGetString(sourceElement, out var source)
            || !TryReadEnum(
                properties,
                "world",
                ParseEvaluationWorld,
                out BrowserEvaluationWorld world)
            || !TryReadOptionalBoolean(properties, "await", true, out var awaitPromise)
            || !TryReadOptionalInt(properties, "timeout_ms", 5_000, out var timeoutMs))
        {
            return Invalid("Browser evaluate requires bounded source, world, and exact revisions.");
        }

        try
        {
            _ = new BrowserEvaluateRequest(
                new SessionId("parser"),
                ParserBinding(documentRevision, viewportRevision, inputEpoch),
                source,
                world,
                awaitPromise,
                TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (ArgumentException)
        {
            return Invalid("Browser evaluate source or timeout violates the scripting boundary.");
        }

        return new BrowserAgentIntentResult.Parsed(
            new BrowserAgentIntent.Evaluate(
                source, world, awaitPromise, TimeSpan.FromMilliseconds(timeoutMs),
                documentRevision, viewportRevision, inputEpoch),
            panelId);
    }

    private static BrowserAutomationBinding ParserBinding(
        long documentRevision,
        long viewportRevision,
        long inputEpoch) =>
        new(
            new BrowserDocumentBinding(BrowserAddress.Blank, documentRevision),
            new BrowserViewportState(
                BrowserViewportState.MaximumCssExtent,
                BrowserViewportState.MaximumCssExtent,
                1),
            viewportRevision,
            inputEpoch);

    private static bool TryReadAutomationRevisions(
        IReadOnlyDictionary<string, JsonElement> properties,
        out long documentRevision,
        out long viewportRevision,
        out long inputEpoch)
    {
        documentRevision = 0;
        viewportRevision = 0;
        inputEpoch = 0;
        return TryReadNonNegativeInt64(properties, "document_revision", out documentRevision)
            && TryReadNonNegativeInt64(properties, "viewport_revision", out viewportRevision)
            && TryReadNonNegativeInt64(properties, "input_epoch", out inputEpoch);
    }

    private static bool TryReadNonNegativeInt64(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out long value)
    {
        value = 0;
        return properties.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && value >= 0;
    }

    private static bool TryReadFiniteNumber(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out double value)
    {
        value = 0;
        return properties.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static bool TryReadOptionalFiniteNumber(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        double defaultValue,
        out double value)
    {
        if (!properties.ContainsKey(name))
        {
            value = defaultValue;
            return true;
        }

        return TryReadFiniteNumber(properties, name, out value);
    }

    private static bool TryReadOptionalInt(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        int defaultValue,
        out int value)
    {
        if (!properties.TryGetValue(name, out var element))
        {
            value = defaultValue;
            return true;
        }

        value = 0;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private static bool TryReadOptionalBoolean(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        bool defaultValue,
        out bool value)
    {
        if (!properties.TryGetValue(name, out var element))
        {
            value = defaultValue;
            return true;
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            value = false;
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private delegate bool StringEnumParser<T>(string? value, out T parsed);

    private static bool TryReadEnum<T>(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        StringEnumParser<T> parser,
        out T value)
    {
        value = default!;
        return properties.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.String
            && parser(element.GetString(), out value);
    }

    private static bool TryReadOptionalEnum<T>(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        StringEnumParser<T> parser,
        T defaultValue,
        out T value)
    {
        if (!properties.ContainsKey(name))
        {
            value = defaultValue;
            return true;
        }

        return TryReadEnum(properties, name, parser, out value);
    }

    private static bool TryReadOptionalModifiers(
        IReadOnlyDictionary<string, JsonElement> properties,
        out BrowserInputModifiers modifiers) =>
        TryReadFlags(
            properties,
            "modifiers",
            static value => value switch
            {
                "alt" => BrowserInputModifiers.Alt,
                "control" => BrowserInputModifiers.Control,
                "meta" => BrowserInputModifiers.Meta,
                "shift" => BrowserInputModifiers.Shift,
                _ => BrowserInputModifiers.None,
            },
            out modifiers);

    private static bool TryReadOptionalButtons(
        IReadOnlyDictionary<string, JsonElement> properties,
        out BrowserMouseButtons buttons) =>
        TryReadFlags(
            properties,
            "buttons",
            static value => value switch
            {
                "left" => BrowserMouseButtons.Left,
                "right" => BrowserMouseButtons.Right,
                "middle" => BrowserMouseButtons.Middle,
                "back" => BrowserMouseButtons.Back,
                "forward" => BrowserMouseButtons.Forward,
                _ => BrowserMouseButtons.None,
            },
            out buttons);

    private static bool TryReadFlags<T>(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        Func<string?, T> parse,
        out T flags)
        where T : struct, Enum
    {
        flags = default;
        if (!properties.TryGetValue(name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 5)
        {
            return false;
        }

        ulong bits = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var parsed = parse(item.GetString());
            var value = Convert.ToUInt64(parsed, System.Globalization.CultureInfo.InvariantCulture);
            if (value == 0 || (bits & value) != 0)
            {
                return false;
            }

            bits |= value;
        }

        flags = (T)Enum.ToObject(typeof(T), bits);
        return true;
    }

    private static bool ParseMouseAction(string? value, out BrowserMouseAction parsed)
    {
        parsed = value switch
        {
            "move" => BrowserMouseAction.Move,
            "click" => BrowserMouseAction.Click,
            "wheel" => BrowserMouseAction.Wheel,
            _ => (BrowserMouseAction)(-1),
        };
        return Enum.IsDefined(parsed);
    }

    private static bool ParseMouseButton(string? value, out BrowserMouseButton parsed)
    {
        parsed = value switch
        {
            "none" => BrowserMouseButton.None,
            "left" => BrowserMouseButton.Left,
            "right" => BrowserMouseButton.Right,
            "middle" => BrowserMouseButton.Middle,
            "back" => BrowserMouseButton.Back,
            "forward" => BrowserMouseButton.Forward,
            _ => (BrowserMouseButton)(-1),
        };
        return Enum.IsDefined(parsed);
    }

    private static bool ParseKeyAction(string? value, out BrowserKeyAction parsed)
    {
        parsed = value switch
        {
            "press" => BrowserKeyAction.Press,
            _ => (BrowserKeyAction)(-1),
        };
        return Enum.IsDefined(parsed);
    }

    private static bool ParseKey(string? value, out BrowserKey parsed) =>
        Enum.TryParse(value, ignoreCase: false, out parsed)
        && Enum.IsDefined(parsed);

    private static bool ParseEvaluationWorld(
        string? value,
        out BrowserEvaluationWorld parsed)
    {
        parsed = value switch
        {
            "isolated" => BrowserEvaluationWorld.Isolated,
            "main" => BrowserEvaluationWorld.Main,
            _ => (BrowserEvaluationWorld)(-1),
        };
        return Enum.IsDefined(parsed);
    }

    private static bool TryReadElementBinding(
        IReadOnlyDictionary<string, JsonElement> properties,
        out string reference,
        out long documentRevision)
    {
        reference = string.Empty;
        documentRevision = 0;
        if (!properties.TryGetValue(
                "reference",
                out var referenceElement)
            || referenceElement.ValueKind != JsonValueKind.String
            || !TryGetString(referenceElement, out var referenceValue)
            || referenceValue.Length
                is < 1 or > BrowserElementReferenceId.MaximumValueBytes
            || referenceValue.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_')
            || !properties.TryGetValue(
                "document_revision",
                out var revisionElement)
            || revisionElement.ValueKind != JsonValueKind.Number
            || !revisionElement.TryGetInt64(out documentRevision)
            || documentRevision < 0)
        {
            return false;
        }

        reference = referenceValue;
        return true;
    }

    private static bool TryGetString(
        JsonElement element,
        out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        try
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsAllowedFillText(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumFillTextBytes
            || text.Any(character =>
                char.IsControl(character)
                && character is not '\t' and not '\n' and not '\r'))
        {
            return false;
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(text[index])
                || index + 1 >= text.Length
                || !char.IsLowSurrogate(text[index + 1]))
            {
                return false;
            }

            index++;
        }

        return true;
    }

    private static bool TryReadUniqueProperties(
        JsonElement arguments,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                properties.Clear();
                return false;
            }
        }

        return true;
    }

    private static bool IsKnownTool(string toolName) =>
        toolName is
            BuiltInAgentTools.BrowserReadState
            or BuiltInAgentTools.BrowserSnapshot
            or BuiltInAgentTools.BrowserWait
            or BuiltInAgentTools.BrowserClick
            or BuiltInAgentTools.BrowserFill
            or BuiltInAgentTools.BrowserCheck
            or BuiltInAgentTools.BrowserMouse
            or BuiltInAgentTools.BrowserKey
            or BuiltInAgentTools.BrowserScroll
            or BuiltInAgentTools.BrowserEvaluate
            or BuiltInAgentTools.BrowserNavigate
            or BuiltInAgentTools.BrowserBack
            or BuiltInAgentTools.BrowserForward
            or BuiltInAgentTools.BrowserReload
            or BuiltInAgentTools.BrowserStop;

    private static BrowserAgentIntentResult UnknownTool() =>
        new BrowserAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a tool that is not available to this run.");

    private static BrowserAgentIntentResult UnavailableTool() =>
        new BrowserAgentIntentResult.Rejected(
            "tool_not_available",
            "The browser tool is not available in the freshly resolved scope.");

    private static BrowserAgentIntentResult Invalid(string message) =>
        new BrowserAgentIntentResult.Rejected(
            "invalid_tool_arguments",
            message);
}

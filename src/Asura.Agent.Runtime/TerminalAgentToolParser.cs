using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class TerminalAgentToolParser
{
    private const int MinimumGridColumns = 2;
    private const int MaximumGridDimension = 1_000;
    private const int MaximumTextBytes = 2 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly TimeSpan MaximumWait = TimeSpan.FromHours(1);

    public static TerminalAgentIntentResult Parse(AgentToolProposal proposal)
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

        if (string.Equals(proposal.ToolName, BuiltInAgentTools.TerminalResize, StringComparison.Ordinal))
        {
            return UnavailableTool();
        }

        return ParseProperties(proposal.ToolName, properties, panelId: null);
    }

    /// <summary>
    /// Parses a proposal for one exact trusted terminal target. Exact contracts
    /// do not expose provider-selected panel identity.
    /// </summary>
    public static TerminalAgentIntentResult Parse(
        AgentToolProposal proposal,
        AgentContextPanel panel,
        IReadOnlySet<PanelInstanceId>? resizeEligiblePanelIds = null)
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
            return Invalid("An exact terminal tool does not accept a panel ID.");
        }

        return TerminalAgentToolSet.Supports(
            panel,
            proposal.ToolName,
            resizeEligiblePanelIds)
            ? ParseProperties(
                proposal.ToolName,
                properties,
                panel.PanelId)
            : UnavailableTool();
    }

    /// <summary>
    /// Parses a proposal against a fresh resolved panel scope. Panel selection
    /// is derived only from active terminal panel IDs and is revalidated for
    /// the exact requested operation.
    /// </summary>
    public static TerminalAgentIntentResult Parse(
        AgentToolProposal proposal,
        IReadOnlyList<AgentContextPanel> panels,
        IReadOnlySet<PanelInstanceId>? resizeEligiblePanelIds = null)
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

        var activeTerminals = TerminalAgentToolSet.ActiveTerminals(panels);
        if (activeTerminals.Length == 0)
        {
            return UnavailableTool();
        }

        if (!properties.TryGetValue("panel_id", out var panelIdElement)
            || panelIdElement.ValueKind != JsonValueKind.String
            || panelIdElement.GetString() is not { } panelId)
        {
            return Invalid(
                "A scoped terminal tool requires one exact panel_id.");
        }

        var selected = activeTerminals.FirstOrDefault(
            panel => string.Equals(
                panel.PanelId.Value,
                panelId,
                StringComparison.Ordinal));
        if (selected is null
            || !TerminalAgentToolSet.Supports(
                selected,
                proposal.ToolName,
                resizeEligiblePanelIds))
        {
            return Invalid(
                "The selected panel_id is not available for this terminal tool.");
        }

        properties.Remove("panel_id");
        return ParseProperties(
            proposal.ToolName,
            properties,
            selected.PanelId);
    }

    private static TerminalAgentIntentResult ParseProperties(
        string toolName,
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId) =>
        toolName switch
        {
            BuiltInAgentTools.TerminalReadScreen =>
                ParseEmpty(
                    properties,
                    new TerminalAgentIntent.ReadScreen(),
                    panelId),
            BuiltInAgentTools.TerminalReadScreenDiff =>
                ParseReadScreenDiff(properties, panelId),
            BuiltInAgentTools.TerminalReadScrollback =>
                ParseReadScrollback(properties, panelId),
            BuiltInAgentTools.TerminalFind =>
                ParseFind(properties, panelId),
            BuiltInAgentTools.TerminalFindOnScreen =>
                ParseFindOnScreen(properties, panelId),
            BuiltInAgentTools.TerminalFindRenderedHistory =>
                ParseFindRenderedHistory(properties, panelId),
            BuiltInAgentTools.TerminalJumpToRenderedHistory =>
                ParseJumpToRenderedHistory(properties, panelId),
            BuiltInAgentTools.TerminalScrollViewport =>
                ParseScrollViewport(properties, panelId),
            BuiltInAgentTools.TerminalSendText =>
                ParseSendText(properties, panelId),
            BuiltInAgentTools.TerminalPaste =>
                ParsePaste(properties, panelId),
            BuiltInAgentTools.TerminalSubmitText =>
                ParseSubmitText(properties, panelId),
            BuiltInAgentTools.TerminalSendKeys =>
                ParseSendKey(properties, panelId),
            BuiltInAgentTools.TerminalSendChord =>
                ParseSendChord(properties, panelId),
            BuiltInAgentTools.TerminalSendMouse =>
                ParseSendMouse(properties, panelId),
            BuiltInAgentTools.TerminalWait =>
                ParseWait(properties, panelId),
            BuiltInAgentTools.TerminalInterrupt =>
                ParseEmpty(
                    properties,
                    new TerminalAgentIntent.Interrupt(),
                    panelId),
            BuiltInAgentTools.TerminalResize =>
                ParseResize(properties, panelId),
            _ => UnknownTool(),
        };

    private static TerminalAgentIntentResult ParseEmpty(
        IReadOnlyDictionary<string, JsonElement> properties,
        TerminalAgentIntent intent,
        PanelInstanceId? panelId) =>
        properties.Count == 0
            ? new TerminalAgentIntentResult.Parsed(intent, panelId)
            : Invalid("This tool does not accept arguments.");

    private static TerminalAgentIntentResult ParseReadScreenDiff(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !TryReadNonNegativeInt64(
                properties,
                "after_content_revision",
                out var afterContentRevision)
            || !properties.TryGetValue("max_changed_rows", out var maximumElement)
            || maximumElement.ValueKind != JsonValueKind.Number
            || !maximumElement.TryGetInt32(out var maximumRows)
            || maximumRows is not (16 or 64 or 200))
        {
            return Invalid(
                "Screen diff requires a non-negative content revision and 16, 64, or 200 changed rows.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.ReadScreenDiff(
                new TerminalScreenDiffInput(afterContentRevision, maximumRows)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseSendText(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue("text", out var textElement)
            || !TryReadBoundedText(textElement, out var text))
        {
            return Invalid("Send text requires one bounded printable text field.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SendText(text),
            panelId);
    }

    private static TerminalAgentIntentResult ParsePaste(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue("text", out var textElement)
            || !TryReadBoundedPasteText(textElement, out var text))
        {
            return Invalid(
                "Paste requires one bounded text field; only tabs and line breaks may be control characters.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.Paste(text),
            panelId);
    }

    private static TerminalAgentIntentResult ParseSubmitText(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue("text", out var textElement)
            || !TryReadBoundedPasteText(textElement, out var text))
        {
            return Invalid(
                "Submit text requires one bounded text field; only tabs and line breaks may be control characters.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SubmitText(text),
            panelId);
    }

    private static TerminalAgentIntentResult ParseSendKey(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count is < 1 or > 3
            || !properties.TryGetValue("key", out var keyElement)
            || keyElement.ValueKind != JsonValueKind.String
            || !TryParseKey(keyElement.GetString(), out var key))
        {
            return Invalid("Send keys requires one supported key and optional modifiers.");
        }

        var modifiers = TerminalKeyModifiers.None;
        if (properties.TryGetValue("modifiers", out var modifiersElement)
            && !TryParseModifiers(modifiersElement, out modifiers))
        {
            return Invalid("Terminal key modifiers are invalid.");
        }

        var repeatCount = 1;
        if (properties.TryGetValue("repeat", out var repeatElement)
            && (repeatElement.ValueKind != JsonValueKind.Number
                || !repeatElement.TryGetInt32(out repeatCount)
                || repeatCount is < 1 or > TerminalKeyStroke.MaximumRepeatCount))
        {
            return Invalid("Terminal key repeat must be between 1 and 64.");
        }

        if (properties.Keys.Any(
                name => name is not ("key" or "modifiers" or "repeat")))
        {
            return Invalid("Send keys contains an unknown field.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SendKey(
                new TerminalKeyStroke(key, modifiers, repeatCount)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseSendChord(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !properties.TryGetValue("character", out var characterElement)
            || characterElement.ValueKind != JsonValueKind.String
            || characterElement.GetString() is not { Length: 1 } characterText
            || characterText[0] is < 'a' or > 'z'
            || !properties.TryGetValue("modifier", out var modifierElement)
            || modifierElement.ValueKind != JsonValueKind.String
            || !TryParseChordModifier(
                modifierElement.GetString(),
                out var modifier))
        {
            return Invalid(
                "Send chord requires one lowercase ASCII letter and exactly one control or alt modifier.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SendChord(
                new TerminalCharacterChord(characterText[0], modifier)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseSendMouse(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count is < 4 or > 5
            || !properties.TryGetValue("event", out var eventElement)
            || eventElement.ValueKind != JsonValueKind.String
            || !TryParseMouseEvent(
                eventElement.GetString(),
                out var button,
                out var kind)
            || !TryReadMouseCoordinate(properties, "column", out var column)
            || !TryReadMouseCoordinate(properties, "row", out var row)
            || !TryReadNonNegativeInt64(
                properties,
                "expected_content_revision",
                out var expectedContentRevision)
            || properties.Keys.Any(
                name => name is not (
                    "event"
                    or "column"
                    or "row"
                    or "modifiers"
                    or "expected_content_revision")))
        {
            return Invalid(
                "Send mouse requires one supported event and bounded zero-based cell coordinates.");
        }

        var modifiers = TerminalKeyModifiers.None;
        if (properties.TryGetValue("modifiers", out var modifiersElement)
            && !TryParseModifiers(modifiersElement, out modifiers))
        {
            return Invalid("Terminal mouse modifiers are invalid.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.SendMouse(
                new TerminalMouseInput(
                    button,
                    kind,
                    column,
                    row,
                    modifiers),
                expectedContentRevision),
            panelId);
    }

    private static TerminalAgentIntentResult ParseReadScrollback(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count is < 2 or > 3
            || !properties.TryGetValue("anchor", out var originElement)
            || originElement.ValueKind != JsonValueKind.String
            || !TryParseScrollbackOrigin(originElement.GetString(), out var origin)
            || !properties.TryGetValue("max_lines", out var maximumElement)
            || maximumElement.ValueKind != JsonValueKind.Number
            || !maximumElement.TryGetInt32(out var maximumLines)
            || !TerminalScrollbackReadInput.IsAllowedMaximumLines(maximumLines)
            || properties.Keys.Any(
                name => name is not ("anchor" or "row_anchor" or "max_lines")))
        {
            return Invalid(
                "Scrollback read requires a known origin and 16, 64, or 200 rows.");
        }

        TerminalScrollbackRowAnchor? rowAnchor = null;
        var requiresAnchor = origin is TerminalScrollbackReadOrigin.Before
            or TerminalScrollbackReadOrigin.After;
        if (requiresAnchor)
        {
            if (!properties.TryGetValue("row_anchor", out var anchorElement)
                || anchorElement.ValueKind != JsonValueKind.String
                || !TerminalScrollbackAnchorCodec.TryDecode(
                    anchorElement.GetString(),
                    out rowAnchor))
            {
                return Invalid(
                    "Before and after scrollback reads require one valid opaque row anchor.");
            }
        }
        else if (properties.ContainsKey("row_anchor"))
        {
            return Invalid("Top and bottom scrollback reads do not accept a row anchor.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.ReadScrollback(
                new TerminalScrollbackReadInput(origin, maximumLines, rowAnchor)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseFind(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 3
            || !properties.TryGetValue("text", out var textElement)
            || !TryReadBoundedText(
                textElement,
                TerminalScrollbackFindInput.MaximumQueryLength,
                out var text)
            || !properties.TryGetValue("direction", out var directionElement)
            || directionElement.ValueKind != JsonValueKind.String
            || !TryParseFindDirection(
                directionElement.GetString(),
                out var direction)
            || !properties.TryGetValue("max_matches", out var maximumElement)
            || maximumElement.ValueKind != JsonValueKind.Number
            || !maximumElement.TryGetInt32(out var maximumMatches)
            || maximumMatches is < 1 or > TerminalScrollbackFindInput.MaximumMatches)
        {
            return Invalid(
                "Terminal find requires bounded literal text, direction, and 1 to 64 matches.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.FindScrollback(
                new TerminalScrollbackFindInput(text, direction, maximumMatches)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseFindOnScreen(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !properties.TryGetValue("text", out var textElement)
            || !TryReadBoundedText(
                textElement,
                TerminalScreenFindInput.MaximumQueryLength,
                out var text)
            || !properties.TryGetValue("max_matches", out var maximumElement)
            || maximumElement.ValueKind != JsonValueKind.Number
            || !maximumElement.TryGetInt32(out var maximumMatches)
            || maximumMatches is < 1 or > TerminalScreenFindInput.MaximumMatches)
        {
            return Invalid(
                "Rendered-screen find requires bounded literal text and 1 to 64 matches.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.FindOnScreen(
                new TerminalScreenFindInput(text, maximumMatches)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseFindRenderedHistory(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 3
            || !properties.TryGetValue("text", out var textElement)
            || !TryReadBoundedText(
                textElement,
                TerminalRenderedHistoryFindInput.MaximumQueryLength,
                out var text)
            || !properties.TryGetValue("direction", out var directionElement)
            || directionElement.ValueKind != JsonValueKind.String
            || !TryParseFindDirection(directionElement.GetString(), out var direction)
            || !properties.TryGetValue("max_matches", out var maximumElement)
            || maximumElement.ValueKind != JsonValueKind.Number
            || !maximumElement.TryGetInt32(out var maximumMatches)
            || maximumMatches is < 1
                or > TerminalRenderedHistoryFindInput.MaximumMatches)
        {
            return Invalid(
                "Rendered-history find requires bounded literal text, direction, and 1 to 64 matches.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.FindRenderedHistory(
                new TerminalRenderedHistoryFindInput(
                    text,
                    direction,
                    maximumMatches)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseJumpToRenderedHistory(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 1
            || !properties.TryGetValue("row_anchor", out var anchorElement)
            || anchorElement.ValueKind != JsonValueKind.String
            || !TerminalRenderedHistoryAnchorCodec.TryDecode(
                anchorElement.GetString(),
                out var anchor))
        {
            return Invalid(
                "Rendered-history jump requires one opaque row_anchor returned by terminal.find_rendered_history.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.JumpToRenderedHistory(anchor!),
            panelId);
    }

    private static TerminalAgentIntentResult ParseScrollViewport(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (!properties.TryGetValue("direction", out var directionElement)
            || directionElement.ValueKind != JsonValueKind.String
            || !TryParseScrollDirection(
                directionElement.GetString(),
                out var direction))
        {
            return Invalid(
                "Viewport scroll requires up/down with bounded unit and amount, or an absolute top/bottom direction.");
        }

        if (direction is TerminalViewportScrollDirection.Top
                or TerminalViewportScrollDirection.Bottom)
        {
            if (properties.Count != 1)
            {
                return Invalid("Absolute top and bottom viewport scrolling accepts only direction.");
            }

            return new TerminalAgentIntentResult.Parsed(
                new TerminalAgentIntent.ScrollViewport(
                    new TerminalViewportScrollInput(
                        direction,
                        TerminalViewportScrollUnit.Line,
                        Amount: 1)),
                panelId);
        }

        if (properties.Count != 3
            || !properties.TryGetValue("unit", out var unitElement)
            || unitElement.ValueKind != JsonValueKind.String
            || !TryParseScrollUnit(unitElement.GetString(), out var unit)
            || !properties.TryGetValue("amount", out var amountElement)
            || amountElement.ValueKind != JsonValueKind.Number
            || !amountElement.TryGetInt32(out var amount)
            || amount is < 1 or > 1_000)
        {
            return Invalid(
                "Viewport scroll requires up/down with bounded unit and amount, or an absolute top/bottom direction.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.ScrollViewport(
                new TerminalViewportScrollInput(direction, unit, amount)),
            panelId);
    }

    private static TerminalAgentIntentResult ParseResize(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count != 2
            || !TryReadGridDimension(properties, "columns", out var columns)
            || !TryReadGridDimension(properties, "rows", out var rows))
        {
            return Invalid(
                "Terminal resize requires columns between 2 and 1000 "
                + "and rows between 1 and 1000.");
        }

        return new TerminalAgentIntentResult.Parsed(
            new TerminalAgentIntent.Resize(columns, rows),
            panelId);
    }

    private static TerminalAgentIntentResult ParseWait(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (properties.Count == 1
            && properties.TryGetValue("delay_ms", out var delayElement)
            && delayElement.ValueKind == JsonValueKind.Number
            && delayElement.TryGetInt32(out var delayMilliseconds)
            && delayMilliseconds >= 1
            && delayMilliseconds <= MaximumWait.TotalMilliseconds)
        {
            return new TerminalAgentIntentResult.Parsed(
                new TerminalAgentIntent.WaitForDelay(
                    TimeSpan.FromMilliseconds(delayMilliseconds)),
                panelId);
        }

        if (properties.Count == 3)
        {
            return ParseShellEventWait(properties, panelId);
        }

        if (properties.Count != 2
            || !properties.TryGetValue("timeout_ms", out var timeoutElement)
            || timeoutElement.ValueKind != JsonValueKind.Number
            || !timeoutElement.TryGetInt32(out var timeoutMilliseconds)
            || timeoutMilliseconds < 1
            || timeoutMilliseconds > MaximumWait.TotalMilliseconds)
        {
            return Invalid(
                "Terminal wait requires one bounded condition and a timeout up to one hour.");
        }

        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        if (properties.TryGetValue("text", out var textElement))
        {
            if (!TryReadBoundedText(textElement, out var text))
            {
                return Invalid(
                    "Terminal text wait requires bounded printable text.");
            }

            return new TerminalAgentIntentResult.Parsed(
                new TerminalAgentIntent.WaitForText(text, timeout),
                panelId);
        }

        if (properties.TryGetValue(
                "after_content_revision",
                out var revisionElement))
        {
            if (revisionElement.ValueKind != JsonValueKind.Number
                || !revisionElement.TryGetInt64(out var revision)
                || revision < 0)
            {
                return Invalid(
                    "Terminal change wait requires a non-negative content revision.");
            }

            return new TerminalAgentIntentResult.Parsed(
                new TerminalAgentIntent.WaitForChange(revision, timeout),
                panelId);
        }

        if (properties.TryGetValue("stable_for_ms", out var stableElement))
        {
            if (stableElement.ValueKind != JsonValueKind.Number
                || !stableElement.TryGetInt32(out var stableMilliseconds)
                || stableMilliseconds < 1
                || stableMilliseconds > timeoutMilliseconds)
            {
                return Invalid(
                    "Terminal stable wait requires a positive interval no longer than its timeout.");
            }

            return new TerminalAgentIntentResult.Parsed(
                new TerminalAgentIntent.WaitForStable(
                    TimeSpan.FromMilliseconds(stableMilliseconds),
                    timeout),
                panelId);
        }

        return Invalid(
            "Terminal wait requires text, after_content_revision, or stable_for_ms.");
    }

    private static TerminalAgentIntentResult ParseShellEventWait(
        IReadOnlyDictionary<string, JsonElement> properties,
        PanelInstanceId? panelId)
    {
        if (!properties.TryGetValue(
                "after_shell_event_sequence",
                out var sequenceElement)
            || sequenceElement.ValueKind != JsonValueKind.Number
            || !sequenceElement.TryGetInt64(out var sequence)
            || sequence < 0
            || !properties.TryGetValue("timeout_ms", out var timeoutElement)
            || timeoutElement.ValueKind != JsonValueKind.Number
            || !timeoutElement.TryGetInt32(out var timeoutMilliseconds)
            || timeoutMilliseconds < 1
            || timeoutMilliseconds > MaximumWait.TotalMilliseconds)
        {
            return Invalid(
                "Semantic terminal waits require a non-negative shell-event baseline and a timeout up to one hour.");
        }

        var promptReady = IsTrue(properties, "prompt_ready");
        var commandFinished = IsTrue(properties, "command_finished");
        if (promptReady == commandFinished
            || properties.Keys.Any(name => name is not (
                "prompt_ready"
                    or "command_finished"
                    or "after_shell_event_sequence"
                    or "timeout_ms")))
        {
            return Invalid(
                "Semantic terminal waits require exactly one true prompt_ready or command_finished condition.");
        }

        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        return new TerminalAgentIntentResult.Parsed(
            promptReady
                ? new TerminalAgentIntent.WaitForPromptReady(sequence, timeout)
                : new TerminalAgentIntent.WaitForCommandFinished(sequence, timeout),
            panelId);
    }

    private static bool IsTrue(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name) =>
        properties.TryGetValue(name, out var element)
        && element.ValueKind is JsonValueKind.True;

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

    private static bool TryReadBoundedText(
        JsonElement element,
        out string text)
        => TryReadBoundedText(element, MaximumTextBytes, out text);

    private static bool TryReadBoundedText(
        JsonElement element,
        int maximumBytes,
        out string text)
    {
        text = string.Empty;
        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString()))
        {
            return false;
        }

        var value = element.GetString()!;
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        if (byteCount > maximumBytes || value.Any(char.IsControl))
        {
            return false;
        }

        text = string.Concat(value);
        return true;
    }

    private static bool TryReadBoundedPasteText(
        JsonElement element,
        out string text)
    {
        text = string.Empty;
        if (element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString()))
        {
            return false;
        }

        var value = element.GetString()!;
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        if (byteCount > MaximumTextBytes
            || value.Any(character =>
                char.IsControl(character)
                && character is not ('\t' or '\r' or '\n')))
        {
            return false;
        }

        text = string.Concat(value);
        return true;
    }

    private static bool TryReadMouseCoordinate(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out int coordinate)
    {
        coordinate = 0;
        return properties.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out coordinate)
            && coordinate is >= 0 and <= 1_000_000;
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

    private static bool TryParseScrollbackOrigin(
        string? value,
        out TerminalScrollbackReadOrigin origin)
    {
        origin = value switch
        {
            "top" => TerminalScrollbackReadOrigin.Top,
            "bottom" => TerminalScrollbackReadOrigin.Bottom,
            "before" => TerminalScrollbackReadOrigin.Before,
            "after" => TerminalScrollbackReadOrigin.After,
            _ => default,
        };
        return value is "top" or "bottom" or "before" or "after";
    }

    private static bool TryParseFindDirection(
        string? value,
        out TerminalScrollbackFindDirection direction)
    {
        direction = string.Equals(value, "backward"
, StringComparison.Ordinal) ? TerminalScrollbackFindDirection.Backward
            : TerminalScrollbackFindDirection.Forward;
        return value is "forward" or "backward";
    }

    private static bool TryParseScrollDirection(
        string? value,
        out TerminalViewportScrollDirection direction)
    {
        direction = value switch
        {
            "up" => TerminalViewportScrollDirection.Up,
            "down" => TerminalViewportScrollDirection.Down,
            "top" => TerminalViewportScrollDirection.Top,
            "bottom" => TerminalViewportScrollDirection.Bottom,
            _ => default,
        };
        return value is "up" or "down" or "top" or "bottom";
    }

    private static bool TryParseScrollUnit(
        string? value,
        out TerminalViewportScrollUnit unit)
    {
        unit = string.Equals(value, "page"
, StringComparison.Ordinal) ? TerminalViewportScrollUnit.Page
            : TerminalViewportScrollUnit.Line;
        return value is "line" or "page";
    }

    private static bool TryReadGridDimension(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        out int dimension)
    {
        dimension = 0;
        var minimum = string.Equals(name, "columns", StringComparison.Ordinal) ? MinimumGridColumns : 1;
        return properties.TryGetValue(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out dimension)
            && dimension >= minimum
            && dimension <= MaximumGridDimension;
    }

    private static bool TryParseMouseEvent(
        string? value,
        out TerminalMouseButton button,
        out TerminalMouseEventKind kind)
    {
        (button, kind) = value switch
        {
            "move" => (
                TerminalMouseButton.None,
                TerminalMouseEventKind.Move),
            "left_down" => (
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Down),
            "left_up" => (
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Up),
            "left_drag" => (
                TerminalMouseButton.Left,
                TerminalMouseEventKind.Drag),
            "middle_down" => (
                TerminalMouseButton.Middle,
                TerminalMouseEventKind.Down),
            "middle_up" => (
                TerminalMouseButton.Middle,
                TerminalMouseEventKind.Up),
            "middle_drag" => (
                TerminalMouseButton.Middle,
                TerminalMouseEventKind.Drag),
            "right_down" => (
                TerminalMouseButton.Right,
                TerminalMouseEventKind.Down),
            "right_up" => (
                TerminalMouseButton.Right,
                TerminalMouseEventKind.Up),
            "right_drag" => (
                TerminalMouseButton.Right,
                TerminalMouseEventKind.Drag),
            "wheel_up" => (
                TerminalMouseButton.WheelUp,
                TerminalMouseEventKind.WheelUp),
            "wheel_down" => (
                TerminalMouseButton.WheelDown,
                TerminalMouseEventKind.WheelDown),
            _ => default,
        };
        return value is
            "move"
            or "left_down" or "left_up" or "left_drag"
            or "middle_down" or "middle_up" or "middle_drag"
            or "right_down" or "right_up" or "right_drag"
            or "wheel_up" or "wheel_down";
    }

    private static bool TryParseModifiers(
        JsonElement element,
        out TerminalKeyModifiers modifiers)
    {
        modifiers = TerminalKeyModifiers.None;
        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var count = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (++count > 4 || item.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var modifier = item.GetString() switch
            {
                "shift" => TerminalKeyModifiers.Shift,
                "alt" => TerminalKeyModifiers.Alt,
                "control" => TerminalKeyModifiers.Control,
                "meta" => TerminalKeyModifiers.Meta,
                _ => (TerminalKeyModifiers?)null,
            };
            if (modifier is null || (modifiers & modifier.Value) != TerminalKeyModifiers.None)
            {
                return false;
            }

            modifiers |= modifier.Value;
        }

        return true;
    }

    private static bool TryParseKey(string? value, out TerminalKey key)
    {
        key = value switch
        {
            "enter" => TerminalKey.Enter,
            "tab" => TerminalKey.Tab,
            "backspace" => TerminalKey.Backspace,
            "escape" => TerminalKey.Escape,
            "space" => TerminalKey.Space,
            "up" => TerminalKey.Up,
            "down" => TerminalKey.Down,
            "left" => TerminalKey.Left,
            "right" => TerminalKey.Right,
            "home" => TerminalKey.Home,
            "end" => TerminalKey.End,
            "page_up" => TerminalKey.PageUp,
            "page_down" => TerminalKey.PageDown,
            "insert" => TerminalKey.Insert,
            "delete" => TerminalKey.Delete,
            "f1" => TerminalKey.F1,
            "f2" => TerminalKey.F2,
            "f3" => TerminalKey.F3,
            "f4" => TerminalKey.F4,
            "f5" => TerminalKey.F5,
            "f6" => TerminalKey.F6,
            "f7" => TerminalKey.F7,
            "f8" => TerminalKey.F8,
            "f9" => TerminalKey.F9,
            "f10" => TerminalKey.F10,
            "f11" => TerminalKey.F11,
            "f12" => TerminalKey.F12,
            "f13" => TerminalKey.F13,
            "f14" => TerminalKey.F14,
            "f15" => TerminalKey.F15,
            "f16" => TerminalKey.F16,
            "f17" => TerminalKey.F17,
            "f18" => TerminalKey.F18,
            "f19" => TerminalKey.F19,
            "f20" => TerminalKey.F20,
            _ => default,
        };
        return value is
            "enter" or "tab" or "backspace" or "escape" or "space"
            or "up" or "down" or "left" or "right" or "home" or "end"
            or "page_up" or "page_down" or "insert" or "delete"
            or "f1" or "f2" or "f3" or "f4" or "f5" or "f6" or "f7"
            or "f8" or "f9" or "f10" or "f11" or "f12" or "f13" or "f14"
            or "f15" or "f16" or "f17" or "f18" or "f19" or "f20";
    }

    private static bool TryParseChordModifier(
        string? value,
        out TerminalCharacterChordModifier modifier)
    {
        modifier = value switch
        {
            "control" => TerminalCharacterChordModifier.Control,
            "alt" => TerminalCharacterChordModifier.Alt,
            _ => default,
        };
        return value is "control" or "alt";
    }

    private static bool IsKnownTool(string toolName) =>
        TerminalAgentToolSet.IsToolName(toolName);

    private static TerminalAgentIntentResult UnknownTool() =>
        new TerminalAgentIntentResult.Rejected(
            "unknown_tool",
            "The provider requested a tool that is not available to this run.");

    private static TerminalAgentIntentResult UnavailableTool() =>
        new TerminalAgentIntentResult.Rejected(
            "tool_not_available",
            "The terminal tool is not available in the freshly resolved scope.");

    private static TerminalAgentIntentResult Invalid(string message) =>
        new TerminalAgentIntentResult.Rejected("invalid_tool_arguments", message);
}

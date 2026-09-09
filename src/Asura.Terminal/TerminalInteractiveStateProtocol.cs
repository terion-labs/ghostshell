using System.Text.Json;
using Asura.Application;

namespace Asura.Terminal;

/// <summary>
/// Parses the opt-in, vendor-neutral payload carried by an OSC 777 desktop
/// notification whose title is <see cref="NotificationTitle"/>. Applications
/// that do not emit the protocol remain ordinary terminals with unknown state.
/// </summary>
internal static class TerminalInteractiveStateProtocol
{
    internal const string NotificationTitle = "terminal.interactive-state.v1";
    internal const string CapabilityEnvironmentVariable =
        "ASURA_INTERACTIVE_STATE_PROTOCOL";
    internal const int MaximumPayloadCharacters = 4 * 1024;
    internal const int MinimumTtlMilliseconds = 250;
    internal const int MaximumTtlMilliseconds = 60_000;

    internal static bool IsProtocolNotification(string title) =>
        string.Equals(title, NotificationTitle, StringComparison.Ordinal);

    internal static bool TryParse(
        string payload,
        long afterSequence,
        DateTimeOffset observedAtUtc,
        out TerminalInteractiveStateUpdate update)
    {
        update = default;
        if (payload.Length is 0 or > MaximumPayloadCharacters)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                payload,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("sequence", out var sequenceElement)
                || !sequenceElement.TryGetInt64(out var sequence)
                || sequence <= afterSequence
                || !root.TryGetProperty("state", out var stateElement)
                || stateElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var state = stateElement.GetString();
            if (string.Equals(state, "clear", StringComparison.Ordinal))
            {
                update = new TerminalInteractiveStateUpdate(sequence, Snapshot: null);
                return true;
            }

            if (!TryMapState(state, out var kind)
                || !root.TryGetProperty("ttl_ms", out var ttlElement)
                || !ttlElement.TryGetInt32(out var ttlMilliseconds)
                || ttlMilliseconds is < MinimumTtlMilliseconds or > MaximumTtlMilliseconds
                || !TryReadInputRegion(root, out var inputRegion))
            {
                return false;
            }

            update = new TerminalInteractiveStateUpdate(
                sequence,
                new TerminalInteractiveStateSnapshot(
                    sequence,
                    kind,
                    observedAtUtc,
                    observedAtUtc.AddMilliseconds(ttlMilliseconds),
                    inputRegion));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadInputRegion(
        JsonElement root,
        out TerminalInputRegion? inputRegion)
    {
        inputRegion = null;
        if (!root.TryGetProperty("input_region", out var regionElement))
        {
            return true;
        }

        if (regionElement.ValueKind != JsonValueKind.Object
            || regionElement.EnumerateObject().Count() != 3
            || !regionElement.TryGetProperty("row", out var rowElement)
            || !rowElement.TryGetInt32(out var row)
            || !regionElement.TryGetProperty("start_column", out var startElement)
            || !startElement.TryGetInt32(out var startColumn)
            || !regionElement.TryGetProperty("end_column_exclusive", out var endElement)
            || !endElement.TryGetInt32(out var endColumn))
        {
            return false;
        }

        try
        {
            inputRegion = new TerminalInputRegion(row, startColumn, endColumn);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryMapState(
        string? state,
        out TerminalInteractiveStateKind kind)
    {
        kind = state switch
        {
            "idle_input" => TerminalInteractiveStateKind.IdleInput,
            "working" => TerminalInteractiveStateKind.Working,
            "streaming" => TerminalInteractiveStateKind.Streaming,
            "modal" => TerminalInteractiveStateKind.Modal,
            "input_required" => TerminalInteractiveStateKind.InputRequired,
            "approval_required" => TerminalInteractiveStateKind.ApprovalRequired,
            _ => default,
        };
        return state is "idle_input"
            or "working"
            or "streaming"
            or "modal"
            or "input_required"
            or "approval_required";
    }
}

internal readonly record struct TerminalInteractiveStateUpdate(
    long Sequence,
    TerminalInteractiveStateSnapshot? Snapshot);

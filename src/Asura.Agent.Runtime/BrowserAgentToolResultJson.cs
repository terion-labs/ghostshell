using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class BrowserAgentToolResultJson
{
    internal const string InteractionOutcomeUnknownStableCode =
        "browser_interaction_outcome_unknown";

    private const int MaximumTitleBytes = 4 * 1024;
    private const int MaximumProviderAddressBytes = 2 * 1024;
    private const int MaximumProviderSnapshotNameBytes = 128;
    internal const int MaximumProviderSnapshotNodes =
        BrowserDocumentSnapshot.MaximumNodeCount;

    public static string Success(
        AgentBrowserActionResult result,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result is AgentBrowserActionResult.Snapshot
            or AgentBrowserActionResult.Wait)
        {
            return BoundedSnapshotBearingSuccess(result, panelId);
        }

        var serialized = SerializeSuccess(
            result,
            panelId,
            maximumSnapshotNodes: 0);
        if (serialized.ByteCount
            > AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            throw new InvalidOperationException(
                "The bounded browser result exceeded the agent-kernel limit.");
        }

        return serialized.Json;
    }

    private static (string Json, int ByteCount) SerializeSuccess(
        AgentBrowserActionResult result,
        PanelInstanceId? panelId,
        int maximumSnapshotNodes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
        switch (result)
        {
            case AgentBrowserActionResult.Completed:
                break;
            case AgentBrowserActionResult.State state:
                WriteState(writer, state.Value);
                break;
            case AgentBrowserActionResult.Snapshot snapshot:
                WriteSnapshot(
                    writer,
                    snapshot.Value,
                    maximumSnapshotNodes);
                break;
            case AgentBrowserActionResult.Wait wait:
                WriteWait(
                    writer,
                    wait.Value,
                    maximumSnapshotNodes);
                break;
            case AgentBrowserActionResult.Automation automation:
                WriteState(writer, automation.Value.FreshState);
                break;
            case AgentBrowserActionResult.Evaluation evaluation:
                WriteState(writer, evaluation.Value.FreshState);
                writer.WritePropertyName("result");
                using (var document = JsonDocument.Parse(evaluation.Value.Json))
                {
                    document.RootElement.WriteTo(writer);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.GetType(),
                    "The browser action result kind is unsupported.");
        }

        writer.WriteEndObject();
        writer.Flush();
        return (
            Encoding.UTF8.GetString(buffer.WrittenSpan),
            buffer.WrittenCount);
    }

    public static string Failure(
        BrowserError error,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var stableCode = error.StableCode;
        return AgentToolResultJson.Failure(
            stableCode,
!string.Equals(stableCode, InteractionOutcomeUnknownStableCode
, StringComparison.Ordinal) && error.Retryable,
            panelId);
    }

    public static string Failure(
        HostError error,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var stableCode = ProviderStableCode(error);
        return AgentToolResultJson.Failure(
            stableCode,
!string.Equals(stableCode, InteractionOutcomeUnknownStableCode
, StringComparison.Ordinal) && error.Retryable,
            panelId);
    }

    internal static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return IsKnownProviderStableCode(error.StableCode)
            ? error.StableCode
            : error.Code switch
            {
                HostErrorCode.InvalidRequest => "invalid_request",
                HostErrorCode.NotFound => "not_found",
                HostErrorCode.RevisionConflict => "revision_conflict",
                HostErrorCode.UnsupportedProtocol =>
                    "unsupported_protocol",
                HostErrorCode.CapabilityNotSupported =>
                    "capability_not_supported",
                HostErrorCode.ConfirmationRequired =>
                    "confirmation_required",
                HostErrorCode.LeaseDenied => "lease_denied",
                HostErrorCode.IdempotencyKeyReused =>
                    "idempotency_key_reused",
                HostErrorCode.DeadlineExceeded => "deadline_exceeded",
                HostErrorCode.Cancelled => "cancelled",
                HostErrorCode.SessionClosed => "session_closed",
                HostErrorCode.EngineFailed => "engine_failed",
                HostErrorCode.ResynchronizationRequired =>
                    "resync_required",
                _ => "browser_action_failed",
            };
    }

    public static string Rejected(
        string stableCode,
        PanelInstanceId? panelId = null) =>
        AgentToolResultJson.Failure(
            stableCode,
            retryable: false,
            panelId);

    private static void WriteState(
        Utf8JsonWriter writer,
        BrowserSessionState state)
    {
        var redactedTitle = TerminalContentRedactor.Redact(state.Title);
        var title = TruncateUtf8(
            redactedTitle.Text,
            MaximumTitleBytes,
            out var titleTruncated);
        var address = ProviderAddress(
            state.Address,
            out var addressTruncated);

        writer.WriteString("content_origin", "untrusted_browser");
        writer.WriteString("address", address);
        writer.WriteBoolean("address_truncated", addressTruncated);
        writer.WriteString("title", title);
        writer.WriteBoolean("title_truncated", titleTruncated);
        writer.WriteNumber(
            "title_redactions",
            redactedTitle.RedactionCount);
        writer.WriteString("load_state", LoadStateName(state.LoadState));
        writer.WriteBoolean("can_go_back", state.CanGoBack);
        writer.WriteBoolean("can_go_forward", state.CanGoForward);
        writer.WriteNumber("document_revision", state.DocumentRevision);
        writer.WriteNumber("viewport_width_css", state.Viewport.WidthCss);
        writer.WriteNumber("viewport_height_css", state.Viewport.HeightCss);
        writer.WriteNumber("device_scale_factor", state.Viewport.DeviceScaleFactor);
        writer.WriteNumber("viewport_revision", state.ViewportRevision);
        writer.WriteNumber("input_epoch", state.InputEpoch);
        if (state.Failure is { } failure)
        {
            // Browser engine messages can contain page-controlled or platform
            // diagnostic content. Only the closed stable projection crosses
            // the provider boundary.
            AgentToolResultJson.WriteError(
                writer,
                "failure",
                failure.StableCode,
                failure.Retryable);
        }
    }

    private static void WriteSnapshot(
        Utf8JsonWriter writer,
        BrowserDocumentSnapshot snapshot,
        int maximumNodes)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (maximumNodes is < 0 or > MaximumProviderSnapshotNodes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumNodes));
        }

        var address = ProviderAddress(
            snapshot.Document.Address,
            out var addressTruncated);
        writer.WriteString("content_origin", "untrusted_browser");
        writer.WriteString("address", address);
        writer.WriteBoolean("address_truncated", addressTruncated);
        writer.WriteNumber(
            "document_revision",
            snapshot.Document.DocumentRevision);
        writer.WriteString(
            "captured_at_utc",
            snapshot.CapturedAtUtc.ToUniversalTime());
        writer.WritePropertyName("nodes");
        writer.WriteStartArray();

        var redactionCount = 0;
        var projectionTruncated = false;
        var writtenNodes = 0;
        foreach (var node in snapshot.Nodes)
        {
            if (writtenNodes == maximumNodes)
            {
                projectionTruncated = true;
                break;
            }

            var redactedRole = TerminalContentRedactor.Redact(node.Role);
            var role = TruncateUtf8(
                redactedRole.Text,
                BrowserSnapshotNode.MaximumRoleBytes,
                out var roleTruncated);
            var redactedName = TerminalContentRedactor.Redact(node.Name);
            var name = TruncateUtf8(
                redactedName.Text,
                MaximumProviderSnapshotNameBytes,
                out var nameTruncated);
            redactionCount = checked(
                redactionCount
                + redactedRole.RedactionCount
                + redactedName.RedactionCount);
            projectionTruncated |= roleTruncated || nameTruncated;

            writer.WriteStartObject();
            writer.WriteNumber("depth", node.Depth);
            writer.WriteString("role", role);
            writer.WriteString("name", name);
            WriteStates(writer, node.States);
            if (node.Reference is { } reference)
            {
                writer.WriteString("reference", reference.Value);
            }

            writer.WriteEndObject();
            writtenNodes++;
        }

        writer.WriteEndArray();
        writer.WriteNumber("available_node_count", snapshot.Nodes.Count);
        writer.WriteNumber("returned_node_count", writtenNodes);
        writer.WriteBoolean(
            "is_truncated",
            snapshot.IsTruncated || projectionTruncated);
        if (snapshot.IsTruncated || projectionTruncated)
        {
            writer.WriteString(
                "narrow_with",
                "filter, interactive_only, or max_depth; use returned refs and do not guess coordinates");
        }
        writer.WriteNumber("redactions", redactionCount);
    }

    private static void WriteWait(
        Utf8JsonWriter writer,
        BrowserWaitOutcome outcome,
        int maximumSnapshotNodes)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        writer.WriteString(
            "wait_outcome",
            outcome.Completion switch
            {
                BrowserWaitCompletion.Matched => "matched",
                BrowserWaitCompletion.TimedOut => "timed_out",
                BrowserWaitCompletion.Cancelled => "cancelled",
                BrowserWaitCompletion.SessionEnded => "session_ended",
                _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
            });
        writer.WriteString(
            "completed_at_utc",
            outcome.CompletedAtUtc.ToUniversalTime());
        WriteState(writer, outcome.State);
        if (outcome.Snapshot is { } snapshot)
        {
            writer.WritePropertyName("snapshot");
            writer.WriteStartObject();
            WriteSnapshot(writer, snapshot, maximumSnapshotNodes);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("snapshot");
            AgentToolResultJson.WriteError(
                writer,
                "snapshot_error",
                outcome.SnapshotError!.StableCode,
                outcome.SnapshotError.Retryable);
        }
    }

    private static string BoundedSnapshotBearingSuccess(
        AgentBrowserActionResult result,
        PanelInstanceId? panelId)
    {
        var nodeCount = result switch
        {
            AgentBrowserActionResult.Snapshot snapshot =>
                snapshot.Value.Nodes.Count,
            AgentBrowserActionResult.Wait { Value.Snapshot: { } snapshot } =>
                snapshot.Nodes.Count,
            AgentBrowserActionResult.Wait => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        var maximumNodes = Math.Min(
            MaximumProviderSnapshotNodes,
            nodeCount);
        var full = SerializeSuccess(
            result,
            panelId,
            maximumNodes);
        var maximumBytes =
            AgentKernelLimits.Default.MaximumToolResultBytes;
        if (full.ByteCount <= maximumBytes)
        {
            return full.Json;
        }

        string? best = null;
        var low = 0;
        var high = maximumNodes - 1;
        while (low <= high)
        {
            var candidateNodes = low + ((high - low) / 2);
            var candidate = SerializeSuccess(
                result,
                panelId,
                candidateNodes);
            if (candidate.ByteCount <= maximumBytes)
            {
                best = candidate.Json;
                low = candidateNodes + 1;
            }
            else
            {
                high = candidateNodes - 1;
            }
        }

        return best
            ?? throw new InvalidOperationException(
                "The bounded browser snapshot envelope exceeded the "
                + "agent-kernel limit.");
    }

    private static void WriteStates(
        Utf8JsonWriter writer,
        BrowserSnapshotNodeState states)
    {
        writer.WritePropertyName("states");
        writer.WriteStartArray();
        WriteState(
            writer,
            states,
            BrowserSnapshotNodeState.Disabled,
            "disabled");
        WriteState(
            writer,
            states,
            BrowserSnapshotNodeState.Checked,
            "checked");
        WriteState(
            writer,
            states,
            BrowserSnapshotNodeState.Selected,
            "selected");
        WriteState(
            writer,
            states,
            BrowserSnapshotNodeState.Expanded,
            "expanded");
        WriteState(
            writer,
            states,
            BrowserSnapshotNodeState.Pressed,
            "pressed");
        WriteState(
            writer,
            states,
            BrowserSnapshotNodeState.Required,
            "required");
        WriteState(
            writer,
            states,
            BrowserSnapshotNodeState.ReadOnly,
            "read_only");
        writer.WriteEndArray();
    }

    private static void WriteState(
        Utf8JsonWriter writer,
        BrowserSnapshotNodeState states,
        BrowserSnapshotNodeState state,
        string name)
    {
        if ((states & state) == state)
        {
            writer.WriteStringValue(name);
        }
    }

    private static string ProviderAddress(
        BrowserAddress address,
        out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(address);
        var value = address.Value;
        var projected = value.Scheme.Equals(
                Uri.UriSchemeHttp,
                StringComparison.OrdinalIgnoreCase)
            || value.Scheme.Equals(
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase)
            ? value.GetLeftPart(UriPartial.Path)
            : value.AbsoluteUri;
        return TruncateUtf8(
            projected,
            MaximumProviderAddressBytes,
            out truncated);
    }

    private static bool IsKnownProviderStableCode(string value) =>
        value is "invalid_request"
            or "not_found"
            or "revision_conflict"
            or "unsupported_protocol"
            or "capability_not_supported"
            or "confirmation_required"
            or "lease_denied"
            or "idempotency_key_reused"
            or "deadline_exceeded"
            or "cancelled"
            or "session_closed"
            or "engine_failed"
            or "resync_required"
            or "unsupported_capability"
            or "renderer_unavailable"
            or "history_unavailable"
            or "navigation_in_progress"
            or "browser_state_changed"
            or "browser_domain_policy_denied"
            or "browser_action_not_authorized"
            or "browser_snapshot_invalid"
            or "browser_element_reference_stale"
            or "browser_element_not_interactable"
            or "browser_element_not_fillable"
            or "browser_element_not_checkable"
            or "browser_check_state_not_applied"
            or "browser_fill_value_not_supported"
            or "browser_script_rejected"
            or "browser_script_result_rejected"
            or InteractionOutcomeUnknownStableCode
            or "navigation_failed"
            or "operation_cancelled"
            or "caller_cancelled"
            or "attachment_revoked"
            or "authority_revoked"
            or "session_revoked"
            or AgentActionFailureCodes.CompletionAuditUnavailable;

    private static string TruncateUtf8(
        string value,
        int maximumBytes,
        out bool truncated)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            truncated = false;
            return value;
        }

        var builder = new StringBuilder();
        var byteCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            byteCount += runeBytes;
        }

        truncated = true;
        return builder.ToString();
    }

    private static string LoadStateName(BrowserLoadState loadState) =>
        loadState switch
        {
            BrowserLoadState.Ready => "ready",
            BrowserLoadState.Loading => "loading",
            BrowserLoadState.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(loadState),
                loadState,
                null),
        };
}

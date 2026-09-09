using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static class TerminalAgentToolResultJson
{
    private const int MaximumScreenTextBytes = 32 * 1024;
    private const int MaximumHistoryTextBytes = 48 * 1024;
    private const int MaximumHistoryRowTextBytes = 8 * 1024;
    private const int MaximumTerminalMetadataBytes = 2 * 1024;
    private const int MaximumShellIntegrationEvents = 32;

    public static string Success(
        AgentTerminalActionResult result,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var screenTextBytes = MaximumScreenTextBytes;
        var historyRows = result switch
        {
            AgentTerminalActionResult.ScreenDiff diff =>
                diff.Result.ChangedRows.Count,
            AgentTerminalActionResult.ScreenFind find =>
                find.Result.Matches.Count,
            AgentTerminalActionResult.RenderedHistoryFind find =>
                find.Result.Matches.Count,
            AgentTerminalActionResult.Scrollback scrollback =>
                scrollback.Snapshot.Rows.Count,
            AgentTerminalActionResult.Find find => find.Result.Matches.Count,
            _ => 0,
        };
        while (true)
        {
            var serialized = SerializeSuccess(
                result,
                panelId,
                screenTextBytes,
                historyRows);
            if (serialized.ByteCount
                <= AgentKernelLimits.Default.MaximumToolResultBytes)
            {
                return serialized.Json;
            }

            if (result is AgentTerminalActionResult.Screen
                or AgentTerminalActionResult.Wait)
            {
                if (screenTextBytes == 0)
                {
                    break;
                }

                screenTextBytes /= 2;
                continue;
            }

            if (result is AgentTerminalActionResult.ScreenDiff
                or AgentTerminalActionResult.ScreenFind
                or AgentTerminalActionResult.RenderedHistoryFind
                or AgentTerminalActionResult.Scrollback
                or AgentTerminalActionResult.Find)
            {
                if (historyRows == 0)
                {
                    break;
                }

                historyRows /= 2;
                continue;
            }

            break;
        }

        throw new InvalidOperationException(
            "The bounded terminal result exceeded the agent-kernel limit.");
    }

    private static (string Json, int ByteCount) SerializeSuccess(
        AgentTerminalActionResult result,
        PanelInstanceId? panelId,
        int maximumScreenTextBytes,
        int maximumHistoryRows)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
        switch (result)
        {
            case AgentTerminalActionResult.Completed:
                break;
            case AgentTerminalActionResult.Screen screen:
                WriteScreen(writer, screen.Snapshot, maximumScreenTextBytes);
                break;
            case AgentTerminalActionResult.ScreenDiff diff:
                WriteScreenDiff(writer, diff.Result, maximumHistoryRows);
                break;
            case AgentTerminalActionResult.ScreenFind find:
                WriteScreenFind(writer, find.Result, maximumHistoryRows);
                break;
            case AgentTerminalActionResult.RenderedHistoryFind find:
                WriteRenderedHistoryFind(
                    writer,
                    find.Result,
                    maximumHistoryRows);
                break;
            case AgentTerminalActionResult.Scrollback scrollback:
                WriteScrollback(
                    writer,
                    scrollback.Snapshot,
                    maximumHistoryRows);
                break;
            case AgentTerminalActionResult.Find find:
                WriteFind(writer, find.Result, maximumHistoryRows);
                break;
            case AgentTerminalActionResult.Wait wait:
                writer.WriteString(
                    "wait_outcome",
                    WaitOutcomeName(wait.Outcome.Kind));
                if (wait.Outcome.InitialContentRevision is { } initialRevision)
                {
                    writer.WriteNumber(
                        "initial_content_revision",
                        initialRevision);
                }

                if (wait.Outcome.Snapshot is { } snapshot)
                {
                    WriteScreen(writer, snapshot, maximumScreenTextBytes);
                }

                if (wait.Outcome.ObservedShellEvent is { } shellEvent)
                {
                    writer.WriteNumber(
                        "observed_shell_event_sequence",
                        shellEvent.Sequence);
                    writer.WriteString(
                        "observed_shell_event_kind",
                        ShellEventKindName(shellEvent.Kind));
                    if (shellEvent.ExitCode is { } exitCode)
                    {
                        writer.WriteNumber("observed_exit_code", exitCode);
                    }
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.GetType(),
                    "The terminal action result kind is unsupported.");
        }

        writer.WriteEndObject();
        writer.Flush();
        return (
            Encoding.UTF8.GetString(buffer.WrittenSpan),
            buffer.WrittenCount);
    }

    public static string Failure(
        HostError error,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return AgentToolResultJson.Failure(
            error.StableCode,
            error.Retryable,
            panelId);
    }

    public static string Rejected(
        string stableCode,
        PanelInstanceId? panelId = null) =>
        AgentToolResultJson.Failure(
            stableCode,
            retryable: false,
            panelId);

    private static void WriteScreen(
        Utf8JsonWriter writer,
        TerminalScreenSnapshot snapshot,
        int maximumTextBytes)
    {
        var redacted = TerminalContentRedactor.Redact(snapshot.PlainText);
        var bounded = TruncateUtf8(
            redacted.Text,
            maximumTextBytes,
            out var resultTruncated);
        var workingDirectoryTruncated = false;
        var workingDirectory = snapshot.WorkingDirectory is { } directory
            ? TruncateUtf8(
                directory,
                MaximumTerminalMetadataBytes,
                out workingDirectoryTruncated)
            : null;
        var windowTitleTruncated = false;
        var windowTitle = snapshot.WindowTitle is { } title
            ? TruncateUtf8(
                title,
                MaximumTerminalMetadataBytes,
                out windowTitleTruncated)
            : null;
        writer.WriteString("content_origin", "untrusted_terminal");
        writer.WriteNumber("content_revision", snapshot.ContentRevision);
        writer.WriteString("captured_at_utc", snapshot.CapturedAtUtc);
        writer.WriteNumber("rows", snapshot.Rows);
        writer.WriteNumber("columns", snapshot.Columns);
        writer.WriteNumber("cursor_row", snapshot.CursorRow);
        writer.WriteNumber("cursor_column", snapshot.CursorColumn);
        writer.WriteBoolean("alternate_screen", snapshot.IsAlternateScreen);
        writer.WriteBoolean("cursor_visible", snapshot.IsCursorVisible);
        writer.WriteBoolean(
            "mouse_tracking_enabled",
            snapshot.IsMouseTrackingEnabled);
        writer.WriteBoolean(
            "bracketed_paste_enabled",
            snapshot.IsBracketedPasteEnabled);
        writer.WriteNumber(
            "scrollback_lines_above",
            snapshot.ScrollbackLinesAbove);
        writer.WriteNumber(
            "scrollback_lines_below",
            snapshot.ScrollbackLinesBelow);
        writer.WriteBoolean("viewport_at_bottom", snapshot.IsViewportAtBottom);
        if (workingDirectory is not null)
        {
            writer.WriteString("working_directory", workingDirectory);
        }

        if (windowTitle is not null)
        {
            writer.WriteString("window_title", windowTitle);
        }

        writer.WriteBoolean(
            "truncated",
            snapshot.IsTruncated
            || resultTruncated
            || maximumTextBytes < MaximumScreenTextBytes
            || workingDirectoryTruncated
            || windowTitleTruncated);
        writer.WriteNumber("redactions", redacted.RedactionCount);
        writer.WriteString("text", bounded);
        WriteInteractiveState(writer, snapshot.InteractiveState);
        WriteShellIntegrationEvents(writer, snapshot.ShellIntegrationEvents);
    }

    private static void WriteInteractiveState(
        Utf8JsonWriter writer,
        TerminalInteractiveStateSnapshot? state)
    {
        writer.WriteBoolean("interactive_state_available", state is not null);
        writer.WriteBoolean(
            "input_region_available",
            state?.InputRegion is not null);
        if (state is null)
        {
            return;
        }

        writer.WritePropertyName("interactive_state");
        writer.WriteStartObject();
        writer.WriteString("origin", "untrusted_terminal_protocol");
        writer.WriteString("state", InteractiveStateName(state.Kind));
        writer.WriteNumber("sequence", state.Sequence);
        writer.WriteString("observed_at_utc", state.ObservedAtUtc);
        writer.WriteString("expires_at_utc", state.ExpiresAtUtc);
        if (state.InputRegion is { } inputRegion)
        {
            writer.WritePropertyName("input_region");
            writer.WriteStartObject();
            writer.WriteNumber("row", inputRegion.Row);
            writer.WriteNumber("start_column", inputRegion.StartColumn);
            writer.WriteNumber(
                "end_column_exclusive",
                inputRegion.EndColumnExclusive);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static string InteractiveStateName(TerminalInteractiveStateKind kind) =>
        kind switch
        {
            TerminalInteractiveStateKind.IdleInput => "idle_input",
            TerminalInteractiveStateKind.Working => "working",
            TerminalInteractiveStateKind.Streaming => "streaming",
            TerminalInteractiveStateKind.Modal => "modal",
            TerminalInteractiveStateKind.InputRequired => "input_required",
            TerminalInteractiveStateKind.ApprovalRequired => "approval_required",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static void WriteShellIntegrationEvents(
        Utf8JsonWriter writer,
        IReadOnlyList<TerminalShellIntegrationEvent> events)
    {
        var start = Math.Max(0, events.Count - MaximumShellIntegrationEvents);
        writer.WriteBoolean(
            "shell_integration_events_truncated",
            start > 0);
        writer.WritePropertyName("shell_integration_events");
        writer.WriteStartArray();
        for (var index = start; index < events.Count; index++)
        {
            var shellEvent = events[index];
            writer.WriteStartObject();
            writer.WriteNumber("sequence", shellEvent.Sequence);
            writer.WriteString("kind", ShellEventKindName(shellEvent.Kind));
            writer.WriteString("captured_at_utc", shellEvent.CapturedAtUtc);
            if (shellEvent.ExitCode is { } exitCode)
            {
                writer.WriteNumber("exit_code", exitCode);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string ShellEventKindName(TerminalCommandBoundaryKind kind) =>
        kind switch
        {
            TerminalCommandBoundaryKind.PromptStarted => "prompt_started",
            TerminalCommandBoundaryKind.CommandInputStarted =>
                "command_input_started",
            TerminalCommandBoundaryKind.CommandExecuted => "command_executed",
            TerminalCommandBoundaryKind.CommandFinished => "command_finished",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static void WriteScrollback(
        Utf8JsonWriter writer,
        TerminalScrollbackSnapshot snapshot,
        int maximumRows)
    {
        writer.WriteString("content_origin", "untrusted_terminal");
        writer.WriteNumber("content_revision", snapshot.ContentRevision);
        writer.WriteNumber("total_lines", snapshot.TotalLines);
        writer.WriteBoolean("has_more_before", snapshot.HasMoreBefore);
        writer.WriteBoolean("has_more_after", snapshot.HasMoreAfter);
        WriteHistoryRows(
            writer,
            "lines",
            snapshot.Rows,
            maximumRows,
            out var resultTruncated);
        writer.WriteBoolean("truncated", resultTruncated);
    }

    private static void WriteScreenDiff(
        Utf8JsonWriter writer,
        TerminalScreenDiffResult result,
        int maximumRows)
    {
        writer.WriteString("content_origin", "untrusted_terminal");
        writer.WriteNumber(
            "initial_content_revision",
            result.InitialContentRevision);
        writer.WriteNumber(
            "content_revision",
            result.CurrentContentRevision);
        writer.WriteBoolean("baseline_available", result.BaselineAvailable);
        writer.WriteNumber("cursor_row", result.CursorRow);
        writer.WriteNumber("cursor_column", result.CursorColumn);
        writer.WriteBoolean("cursor_visible", result.IsCursorVisible);
        var emittedBytes = 0;
        var resultTruncated = result.ChangedRows.Count > maximumRows;
        writer.WritePropertyName("changed_rows");
        writer.WriteStartArray();
        foreach (var row in result.ChangedRows.Take(maximumRows))
        {
            var redacted = TerminalContentRedactor.Redact(row.Text);
            var bounded = TruncateUtf8(
                redacted.Text,
                MaximumHistoryRowTextBytes,
                out var rowResultTruncated);
            var bytes = Encoding.UTF8.GetByteCount(bounded);
            if (emittedBytes + bytes > MaximumHistoryTextBytes)
            {
                resultTruncated = true;
                break;
            }

            emittedBytes += bytes;
            writer.WriteStartObject();
            writer.WriteNumber("row", row.Row);
            writer.WriteBoolean("wrapped", row.IsWrapped);
            writer.WriteBoolean(
                "truncated",
                row.IsTextTruncated || rowResultTruncated);
            writer.WriteNumber("redactions", redacted.RedactionCount);
            writer.WriteString("text", bounded);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean(
            "truncated",
            result.IsTruncated || resultTruncated);
        WriteInteractiveState(writer, result.InteractiveState);
    }

    private static void WriteScreenFind(
        Utf8JsonWriter writer,
        TerminalScreenFindResult result,
        int maximumRows)
    {
        writer.WriteString("content_origin", "untrusted_terminal");
        writer.WriteNumber("content_revision", result.ContentRevision);
        writer.WriteNumber("match_count", result.Matches.Count);
        var emittedBytes = 0;
        var resultTruncated = result.Matches.Count > maximumRows;
        writer.WritePropertyName("matches");
        writer.WriteStartArray();
        foreach (var match in result.Matches.Take(maximumRows))
        {
            var redacted = TerminalContentRedactor.Redact(match.LineText);
            var bounded = TruncateUtf8(
                redacted.Text,
                MaximumHistoryRowTextBytes,
                out var lineResultTruncated);
            var bytes = Encoding.UTF8.GetByteCount(bounded);
            if (emittedBytes + bytes > MaximumHistoryTextBytes)
            {
                resultTruncated = true;
                break;
            }

            emittedBytes += bytes;
            writer.WriteStartObject();
            writer.WriteNumber("offset", match.Offset);
            writer.WriteNumber("line", match.Line);
            writer.WriteNumber("column", match.Column);
            writer.WriteBoolean(
                "truncated",
                match.IsLineTruncated || lineResultTruncated);
            writer.WriteNumber("redactions", redacted.RedactionCount);
            writer.WriteString("line_text", bounded);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean(
            "truncated",
            result.IsTruncated || resultTruncated);
    }

    private static void WriteFind(
        Utf8JsonWriter writer,
        TerminalScrollbackFindResult result,
        int maximumRows)
    {
        writer.WriteString("content_origin", "untrusted_terminal");
        writer.WriteNumber("content_revision", result.ContentRevision);
        writer.WriteNumber("total_lines", result.TotalLines);
        WriteHistoryRows(
            writer,
            "matches",
            result.Matches,
            maximumRows,
            out var resultTruncated);
        writer.WriteBoolean("truncated", result.IsTruncated || resultTruncated);
    }

    private static void WriteRenderedHistoryFind(
        Utf8JsonWriter writer,
        TerminalRenderedHistoryFindResult result,
        int maximumRows)
    {
        writer.WriteString("content_origin", "untrusted_terminal");
        writer.WriteNumber("content_revision", result.ContentRevision);
        writer.WriteNumber("total_rows", result.TotalRows);
        var emittedBytes = 0;
        var resultTruncated = result.Matches.Count > maximumRows;
        writer.WritePropertyName("matches");
        writer.WriteStartArray();
        foreach (var row in result.Matches.Take(maximumRows))
        {
            var redacted = TerminalContentRedactor.Redact(row.Text);
            var bounded = TruncateUtf8(
                redacted.Text,
                MaximumHistoryRowTextBytes,
                out var rowResultTruncated);
            var bytes = Encoding.UTF8.GetByteCount(bounded);
            if (emittedBytes + bytes > MaximumHistoryTextBytes)
            {
                resultTruncated = true;
                break;
            }

            emittedBytes += bytes;
            writer.WriteStartObject();
            writer.WriteString(
                "row_anchor",
                TerminalRenderedHistoryAnchorCodec.Encode(row.Anchor));
            writer.WriteBoolean(
                "truncated",
                row.IsTruncated || rowResultTruncated);
            writer.WriteNumber("redactions", redacted.RedactionCount);
            writer.WriteString("text", bounded);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("truncated", result.IsTruncated || resultTruncated);
    }

    private static void WriteHistoryRows(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<TerminalScrollbackRow> rows,
        int maximumRows,
        out bool resultTruncated)
    {
        var emittedBytes = 0;
        resultTruncated = rows.Count > maximumRows;
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var row in rows.Take(maximumRows))
        {
            var redacted = TerminalContentRedactor.Redact(row.Text);
            var bounded = TruncateUtf8(
                redacted.Text,
                MaximumHistoryRowTextBytes,
                out var rowResultTruncated);
            var bytes = Encoding.UTF8.GetByteCount(bounded);
            if (emittedBytes + bytes > MaximumHistoryTextBytes)
            {
                resultTruncated = true;
                break;
            }

            emittedBytes += bytes;
            writer.WriteStartObject();
            writer.WriteString(
                "row_anchor",
                TerminalScrollbackAnchorCodec.Encode(row.Anchor));
            writer.WriteBoolean(
                "truncated",
                row.IsTruncated || rowResultTruncated);
            writer.WriteNumber("redactions", redacted.RedactionCount);
            writer.WriteString("text", bounded);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

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

    private static string WaitOutcomeName(TerminalWaitOutcomeKind outcome) =>
        outcome switch
        {
            TerminalWaitOutcomeKind.Elapsed => "elapsed",
            TerminalWaitOutcomeKind.Matched => "matched",
            TerminalWaitOutcomeKind.Changed => "changed",
            TerminalWaitOutcomeKind.Stable => "stable",
            TerminalWaitOutcomeKind.PromptReady => "prompt_ready",
            TerminalWaitOutcomeKind.CommandFinished => "command_finished",
            TerminalWaitOutcomeKind.Timeout => "timeout",
            TerminalWaitOutcomeKind.Cancelled => "cancelled",
            TerminalWaitOutcomeKind.SessionEnded => "session_ended",
            TerminalWaitOutcomeKind.Unsupported => "unsupported",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                null),
        };
}

internal static class TerminalContentRedactor
{
    private const string Redaction = "[REDACTED SECRET-BEARING LINE]";

    private static readonly string[] Markers =
    [
        "authorization:",
        "api_key=",
        "apikey=",
        "client_secret=",
        "password=",
        "password:",
        "passwd=",
        "private_key=",
        "refresh_token=",
        "secret=",
        "token=",
        "-----begin private key-----",
        "-----begin encrypted private key-----",
        "-----begin openssh private key-----",
    ];

    private static readonly string[] TokenPrefixes =
    [
        "ghp_",
        "github_pat_",
        "sk-",
        "akia",
        "xoxb-",
        "xoxp-",
    ];

    private static readonly string[] AssignmentKeys =
    [
        "access-token",
        "access_token",
        "api-key",
        "api_key",
        "apikey",
        "authorization",
        "client-secret",
        "client_secret",
        "password",
        "passwd",
        "private-key",
        "private_key",
        "refresh-token",
        "refresh_token",
        "secret",
        "token",
    ];

    public static RedactedTerminalContent Redact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var lines = value.Split('\n');
        var count = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!LooksSecretBearing(lines[index]))
            {
                continue;
            }

            lines[index] = Redaction;
            count++;
        }

        return new RedactedTerminalContent(
            string.Join('\n', lines),
            count);
    }

    private static bool LooksSecretBearing(string line)
    {
        if (Markers.Any(marker =>
                line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            || AssignmentKeys.Any(key =>
                ContainsSecretAssignment(line, key)))
        {
            return true;
        }

        foreach (var token in line.Split(
                     [' ', '\t', '"', '\'', ',', ';'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 12
                && TokenPrefixes.Any(prefix =>
                    token.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSecretAssignment(
        string value,
        string key)
    {
        var searchStart = 0;
        while (searchStart < value.Length)
        {
            var keyStart = value.IndexOf(
                key,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (keyStart < 0)
            {
                return false;
            }

            searchStart = keyStart + key.Length;
            if (keyStart > 0
                && IsAssignmentIdentifier(value[keyStart - 1]))
            {
                continue;
            }

            var cursor = searchStart;
            if (cursor < value.Length && value[cursor] is '"' or '\'')
            {
                cursor++;
            }
            else if (cursor < value.Length
                     && IsAssignmentIdentifier(value[cursor]))
            {
                continue;
            }

            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor >= value.Length
                || value[cursor] is not (':' or '='))
            {
                continue;
            }

            cursor++;
            while (cursor < value.Length && char.IsWhiteSpace(value[cursor]))
            {
                cursor++;
            }

            if (cursor >= value.Length
                || value[cursor] is ',' or '}' or ']')
            {
                continue;
            }

            return !IsNonSecretLiteral(value, cursor);
        }

        return false;
    }

    private static bool IsAssignmentIdentifier(char value) =>
        char.IsLetterOrDigit(value)
        || value is '_' or '-';

    private static bool IsNonSecretLiteral(string value, int start)
    {
        if (value.AsSpan(start).StartsWith("\"\"", StringComparison.Ordinal)
            || value.AsSpan(start).StartsWith("''", StringComparison.Ordinal))
        {
            return true;
        }

        return IsDelimitedLiteral(value, start, "null")
            || IsDelimitedLiteral(value, start, "false")
            || IsDelimitedLiteral(value, start, "true");
    }

    private static bool IsDelimitedLiteral(
        string value,
        int start,
        string literal)
    {
        if (!value.AsSpan(start).StartsWith(
                literal,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var end = start + literal.Length;
        return end == value.Length
            || char.IsWhiteSpace(value[end])
            || value[end] is ',' or '}' or ']';
    }
}

internal sealed record RedactedTerminalContent(
    string Text,
    int RedactionCount);

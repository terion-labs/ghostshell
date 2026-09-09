using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure;

/// <summary>
/// Owns the fixed field allowlist and canonical ordering for recent-session export schema v1.
/// </summary>
internal static class RecentSessionHistoryJson
{
    public static byte[] SerializeNewestFirst(
        RecentSessionRecord[] recentSessions,
        DateTimeOffset exportedAt,
        CancellationToken cancellationToken)
    {
        Array.Sort(recentSessions, CompareNewestFirst);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber(
            "schemaVersion",
            RecentSessionHistoryExportFormat.CurrentSchemaVersion);
        writer.WriteString(
            "contentPolicy",
            RecentSessionHistoryExportFormat.ContentPolicy);
        writer.WriteString("exportedAt", FormatTimestamp(exportedAt));
        writer.WriteStartArray("sessions");

        foreach (var recentSession in recentSessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteString("sessionId", recentSession.SessionId.Value);
            writer.WriteString(
                "sourceDefinitionKind",
                recentSession.SourceDefinition.Kind.Value);
            writer.WriteString(
                "sourceDefinitionId",
                recentSession.SourceDefinition.Value);
            writer.WriteString("panelKind", PanelKindName(recentSession.Kind));
            writer.WriteString("title", recentSession.Title);
            writer.WriteString("startedAt", FormatTimestamp(recentSession.StartedAt));
            if (recentSession.EndedAt is { } endedAt)
            {
                writer.WriteString("endedAt", FormatTimestamp(endedAt));
            }
            else
            {
                writer.WriteNull("endedAt");
            }

            writer.WriteString("outcome", OutcomeName(recentSession.Outcome));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static int CompareNewestFirst(
        RecentSessionRecord? left,
        RecentSessionRecord? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return 1;
        }

        if (right is null)
        {
            return -1;
        }

        var comparison = right.LastUsedAt.CompareTo(left.LastUsedAt);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = right.StartedAt.CompareTo(left.StartedAt);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            left.SessionId.Value,
            right.SessionId.Value);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            left.SourceDefinition.Kind.Value,
            right.SourceDefinition.Kind.Value);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(
            left.SourceDefinition.Value,
            right.SourceDefinition.Value);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Kind.CompareTo(right.Kind);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = StringComparer.Ordinal.Compare(left.Title, right.Title);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Nullable.Compare(left.EndedAt, right.EndedAt);
        return comparison != 0
            ? comparison
            : left.Outcome.CompareTo(right.Outcome);
    }

    private static string PanelKindName(PanelKind kind) => kind switch
    {
        PanelKind.Terminal => "terminal",
        PanelKind.Browser => "browser",
        PanelKind.FileViewer => "file-viewer",
        PanelKind.Statistics => "statistics",
        PanelKind.ProcessMonitor => "process-monitor",
        PanelKind.DatabaseViewer => "database-viewer",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string OutcomeName(RecentSessionOutcome outcome) => outcome switch
    {
        RecentSessionOutcome.Active => "active",
        RecentSessionOutcome.GracefullyClosed => "gracefully-closed",
        RecentSessionOutcome.ForceTerminated => "force-terminated",
        RecentSessionOutcome.Failed => "failed",
        RecentSessionOutcome.Cancelled => "cancelled",
        RecentSessionOutcome.Interrupted => "interrupted",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);
}

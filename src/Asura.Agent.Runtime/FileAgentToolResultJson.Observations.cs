using System.Buffers;
using System.Text;
using System.Text.Json;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static partial class FileAgentToolResultJson
{
    private const int MaximumAccessIdentityBytes = 256;
    private const int MaximumTransferStageBytes = 256;

    private static FileAgentToolJsonProjection ProjectSearchResults(
        AgentFileActionResult.SearchResults result,
        FileAgentIntent.Search intent,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        if (intent.MaximumResults is < 1
                or > AgentFileActionComposer.MaximumAgentSearchResults
            || result.Entries.IsDefault
            || result.Entries.Length > intent.MaximumResults)
        {
            return Rejected("file_result_invalid", panelId);
        }

        var projected = new List<ProjectedFileEntry>(result.Entries.Length);
        var projectionTruncated = false;
        foreach (var entry in result.Entries)
        {
            if (!TryGetRelativePath(
                    metadata.TrustedRoot,
                    entry.Location,
                    out var relativePath)
                || relativePath.Length <= intent.RelativePath.Length
                || !relativePath.Take(intent.RelativePath.Length)
                    .SequenceEqual(intent.RelativePath)
                || intent.Scope == FilePanelDiscoveryScope.CurrentDirectory
                    && relativePath.Length != intent.RelativePath.Length + 1
                || entry.IsHidden
                || relativePath.IsEmpty
                || !string.Equals(
                    entry.Name,
                    relativePath[^1].Value,
                    StringComparison.Ordinal)
                || !entry.Name.Contains(
                    intent.Query,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Rejected("file_result_invalid", panelId);
            }

            var projectedEntry = ProjectEntry(entry, relativePath);
            projected.Add(projectedEntry);
            projectionTruncated |= projectedEntry.Truncated;
        }

        for (var count = projected.Count; count >= 0; count--)
        {
            var truncated = result.IsTruncated
                || projectionTruncated
                || count < projected.Count;
            var serialized = SerializeSearchResults(
                projected,
                count,
                truncated,
                projected.Take(count).Sum(entry => entry.Redactions),
                panelId);
            if (serialized.ByteCount
                <= AgentKernelLimits.Default.MaximumToolResultBytes)
            {
                return Succeeded(serialized.Json);
            }
        }

        return Rejected("file_limit_exceeded", panelId);
    }

    private static FileAgentToolJsonProjection ProjectAccessControl(
        AgentFileActionResult.AccessControl result,
        FileAgentIntent.AccessRead intent,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        var value = result.Value;
        if (!TryGetRelativePath(
                metadata.TrustedRoot,
                value.Location,
                out var relativePath)
            || !relativePath.SequenceEqual(intent.RelativePath)
            || value.Grants.Count
                > AgentFileActionComposer.MaximumAgentAccessGrants)
        {
            return Rejected("file_result_invalid", panelId);
        }

        var path = ProjectPath(relativePath);
        var owner = ProjectOptionalText(value.Owner, MaximumAccessIdentityBytes);
        var group = ProjectOptionalText(value.Group, MaximumAccessIdentityBytes);
        var grants = new List<ProjectedAccessGrant>(value.Grants.Count);
        var truncated = result.IsTruncated
            || path.Truncated
            || owner.Truncated
            || group.Truncated;
        var redactions = checked(
            path.Redactions + owner.Redactions + group.Redactions);
        foreach (var grant in value.Grants)
        {
            if (grant?.Grantee is null
                || !Enum.IsDefined(grant.Grantee.Kind)
                || (grant.Rights & ~FilePanelAccessRight.FullControl) != FilePanelAccessRight.None)
            {
                return Rejected("file_result_invalid", panelId);
            }

            var id = ProjectOptionalText(
                grant.Grantee.Id,
                MaximumAccessIdentityBytes);
            var displayName = ProjectOptionalText(
                grant.Grantee.DisplayName,
                MaximumAccessIdentityBytes);
            grants.Add(new ProjectedAccessGrant(
                AccessGranteeKindName(grant.Grantee.Kind),
                id.Text,
                displayName.Text,
                grant.Rights));
            truncated |= id.Truncated || displayName.Truncated;
            redactions = checked(
                redactions + id.Redactions + displayName.Redactions);
        }

        for (var count = grants.Count; count >= 0; count--)
        {
            var serialized = SerializeAccessControl(
                path,
                value.Mode,
                owner.Text,
                group.Text,
                grants,
                count,
                truncated || count < grants.Count,
                redactions,
                panelId);
            if (serialized.ByteCount
                <= AgentKernelLimits.Default.MaximumToolResultBytes)
            {
                return Succeeded(serialized.Json);
            }
        }

        return Rejected("file_limit_exceeded", panelId);
    }

    private static FileAgentToolJsonProjection ProjectTransfers(
        AgentFileActionResult.Transfers result,
        PanelInstanceId? panelId)
    {
        if (result.Values.IsDefault
            || result.Values.Length
                > AgentFileActionComposer.MaximumAgentTransfers)
        {
            return Rejected("file_result_invalid", panelId);
        }

        var transfers = new List<ProjectedTransfer>(result.Values.Length);
        var truncated = result.IsTruncated;
        var redactions = 0;
        foreach (var value in result.Values)
        {
            if (!TryProjectTransfer(value, out var projected))
            {
                return Rejected("file_result_invalid", panelId);
            }

            transfers.Add(projected!);
            truncated |= projected!.Truncated;
            redactions = checked(redactions + projected.Redactions);
        }

        for (var count = transfers.Count; count >= 0; count--)
        {
            var serialized = SerializeTransfers(
                transfers,
                count,
                truncated || count < transfers.Count,
                transfers.Take(count).Sum(item => item.Redactions),
                panelId);
            if (serialized.ByteCount
                <= AgentKernelLimits.Default.MaximumToolResultBytes)
            {
                return Succeeded(serialized.Json);
            }
        }

        return Rejected("file_limit_exceeded", panelId);
    }

    private static (string Json, int ByteCount) SerializeSearchResults(
        IReadOnlyList<ProjectedFileEntry> entries,
        int count,
        bool truncated,
        int redactions,
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteString("content_origin", "untrusted_file");
        writer.WriteBoolean("truncated", truncated);
        writer.WriteNumber("redactions", redactions);
        writer.WritePropertyName("matches");
        writer.WriteStartArray();
        for (var index = 0; index < count; index++)
        {
            WriteEntry(writer, entries[index]);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), buffer.WrittenCount);
    }

    private static (string Json, int ByteCount) SerializeAccessControl(
        ProjectedPath path,
        FilePanelPosixMode? mode,
        string? owner,
        string? group,
        IReadOnlyList<ProjectedAccessGrant> grants,
        int count,
        bool truncated,
        int redactions,
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteString("content_origin", "untrusted_file");
        WritePath(writer, path.Segments);
        writer.WriteBoolean("truncated", truncated);
        writer.WriteNumber("redactions", redactions);
        if (mode is not null)
        {
            writer.WriteString("mode_octal", mode.Octal);
            writer.WriteString("mode_symbolic", mode.Symbolic);
        }

        if (owner is not null)
        {
            writer.WriteString("owner", owner);
        }

        if (group is not null)
        {
            writer.WriteString("group", group);
        }

        writer.WritePropertyName("grants");
        writer.WriteStartArray();
        for (var index = 0; index < count; index++)
        {
            WriteAccessGrant(writer, grants[index]);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), buffer.WrittenCount);
    }

    private static (string Json, int ByteCount) SerializeTransfers(
        IReadOnlyList<ProjectedTransfer> transfers,
        int count,
        bool truncated,
        int redactions,
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteString("content_origin", "untrusted_file");
        writer.WriteBoolean("truncated", truncated);
        writer.WriteNumber("redactions", redactions);
        writer.WriteBoolean("cancellation_does_not_rollback_bytes", true);
        writer.WritePropertyName("transfers");
        writer.WriteStartArray();
        for (var index = 0; index < count; index++)
        {
            WriteTransfer(writer, transfers[index]);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return (Encoding.UTF8.GetString(buffer.WrittenSpan), buffer.WrittenCount);
    }

    private static void WriteAccessGrant(
        Utf8JsonWriter writer,
        ProjectedAccessGrant grant)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", grant.Kind);
        if (grant.Id is not null)
        {
            writer.WriteString("id", grant.Id);
        }

        if (grant.DisplayName is not null)
        {
            writer.WriteString("display_name", grant.DisplayName);
        }

        writer.WritePropertyName("rights");
        writer.WriteStartArray();
        WriteRight(writer, grant.Rights, FilePanelAccessRight.Read, "read");
        WriteRight(writer, grant.Rights, FilePanelAccessRight.Write, "write");
        WriteRight(writer, grant.Rights, FilePanelAccessRight.ReadAcl, "read_acl");
        WriteRight(writer, grant.Rights, FilePanelAccessRight.WriteAcl, "write_acl");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRight(
        Utf8JsonWriter writer,
        FilePanelAccessRight available,
        FilePanelAccessRight right,
        string name)
    {
        if ((available & right) != FilePanelAccessRight.None)
        {
            writer.WriteStringValue(name);
        }
    }

    private static bool TryProjectTransfer(
        FilePanelTransferSnapshot? value,
        out ProjectedTransfer? projected)
    {
        projected = null;
        if (value is null
            || value.Id.Value == Guid.Empty
            || value.Request is null
            || !Enum.IsDefined(value.Request.Operation)
            || !Enum.IsDefined(value.Request.ConflictPolicy)
            || !Enum.IsDefined(value.State)
            || value.BytesTransferred < 0
            || value.TotalBytes is < 0
            || value.TotalBytes is { } total
                && value.BytesTransferred > total)
        {
            return false;
        }

        var stage = ProjectText(value.Stage, MaximumTransferStageBytes);
        var errorCode = ProjectOptionalText(
            value.Error?.StableCode,
            MaximumProviderMetadataBytes);
        var truncated = stage.Truncated
            || errorCode.Truncated;
        var redactions = checked(
            stage.Redactions
            + errorCode.Redactions);
        projected = new ProjectedTransfer(
            value.Id.ToString(),
            TransferOperationName(value.Request.Operation),
            ConflictPolicyName(value.Request.ConflictPolicy),
            TransferStateName(value.State),
            stage.Text,
            value.BytesTransferred,
            value.TotalBytes,
            errorCode.Text,
            value.Error?.Retryable,
            value.QueuedAt,
            value.StartedAt,
            value.CompletedAt,
            value.CancellationRequested,
            value.CanCancel,
            value.CanRetry,
            truncated,
            redactions);
        return true;
    }

    private static void WriteTransfer(
        Utf8JsonWriter writer,
        ProjectedTransfer value)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        writer.WriteString("operation", value.Operation);
        writer.WriteString("conflict_policy", value.ConflictPolicy);
        writer.WriteString("state", value.State);
        writer.WriteString("stage", value.Stage);
        writer.WriteNumber("bytes_transferred", value.BytesTransferred);
        if (value.TotalBytes is { } total)
        {
            writer.WriteNumber("total_bytes", total);
        }

        if (value.ErrorCode is not null)
        {
            writer.WriteString("error_code", value.ErrorCode);
            writer.WriteBoolean("error_retryable", value.ErrorRetryable == true);
        }

        writer.WriteString("queued_at_utc", value.QueuedAt.ToUniversalTime());
        if (value.StartedAt is { } started)
        {
            writer.WriteString("started_at_utc", started.ToUniversalTime());
        }

        if (value.CompletedAt is { } completed)
        {
            writer.WriteString("completed_at_utc", completed.ToUniversalTime());
        }

        writer.WriteBoolean("cancellation_requested", value.CancellationRequested);
        writer.WriteBoolean(
            "provider_reports_cancellable",
            value.CanCancel);
        writer.WriteBoolean("governed_cancel_available", false);
        writer.WriteBoolean(
            "provider_reports_retryable",
            value.CanRetry);
        writer.WriteBoolean("governed_retry_available", false);
        writer.WriteEndObject();
    }

    private static ProjectedOptionalText ProjectOptionalText(
        string? value,
        int maximumBytes)
    {
        if (value is null)
        {
            return new ProjectedOptionalText(null, false, 0);
        }

        var projected = ProjectText(value, maximumBytes);
        return new ProjectedOptionalText(
            projected.Text,
            projected.Truncated,
            projected.Redactions);
    }

    private static string AccessGranteeKindName(FilePanelGranteeKind kind) =>
        kind switch
        {
            FilePanelGranteeKind.Owner => "owner",
            FilePanelGranteeKind.Group => "group",
            FilePanelGranteeKind.Everyone => "everyone",
            FilePanelGranteeKind.AuthenticatedUsers => "authenticated_users",
            FilePanelGranteeKind.LogDelivery => "log_delivery",
            FilePanelGranteeKind.User => "user",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    private static string TransferOperationName(FilePanelTransferOperation value) =>
        value switch
        {
            FilePanelTransferOperation.Copy => "copy",
            FilePanelTransferOperation.Move => "move",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string ConflictPolicyName(FilePanelConflictPolicy value) =>
        value switch
        {
            FilePanelConflictPolicy.Fail => "fail",
            FilePanelConflictPolicy.Skip => "skip",
            FilePanelConflictPolicy.Replace => "replace",
            FilePanelConflictPolicy.KeepBoth => "keep_both",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private static string TransferStateName(FilePanelTransferState value) =>
        value switch
        {
            FilePanelTransferState.Queued => "queued",
            FilePanelTransferState.Running => "running",
            FilePanelTransferState.Completed => "completed",
            FilePanelTransferState.Failed => "failed",
            FilePanelTransferState.Cancelled => "cancelled",
            FilePanelTransferState.Skipped => "skipped",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
        };

    private sealed record ProjectedOptionalText(
        string? Text,
        bool Truncated,
        int Redactions);

    private sealed record ProjectedAccessGrant(
        string Kind,
        string? Id,
        string? DisplayName,
        FilePanelAccessRight Rights);

    private sealed record ProjectedTransfer(
        string Id,
        string Operation,
        string ConflictPolicy,
        string State,
        string Stage,
        long BytesTransferred,
        long? TotalBytes,
        string? ErrorCode,
        bool? ErrorRetryable,
        DateTimeOffset QueuedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        bool CancellationRequested,
        bool CanCancel,
        bool CanRetry,
        bool Truncated,
        int Redactions);
}

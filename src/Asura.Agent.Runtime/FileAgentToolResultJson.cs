using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime;

internal static partial class FileAgentToolResultJson
{
    internal const string FileMutationOutcomeUnknownStableCode =
        "file_mutation_outcome_unknown";

    private const int MaximumProviderEntries = 100;
    private const int MaximumProviderMetadataBytes = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static FileAgentToolJsonProjection Project(
        AgentFileActionResult result,
        FileAgentIntent intent,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(metadata);
        return (result, intent) switch
        {
            (AgentFileActionResult.Page page, FileAgentIntent.List list) =>
                ProjectPage(
                    page.Value,
                    list.RelativePath,
                    metadata,
                    panelId),
            (
                AgentFileActionResult.SearchResults searchResults,
                FileAgentIntent.Search search) =>
                ProjectSearchResults(
                    searchResults,
                    search,
                    metadata,
                    panelId),
            (AgentFileActionResult.Entry entry, FileAgentIntent.Stat stat) =>
                ProjectEntry(
                    entry.Value,
                    entry.Reference,
                    stat.RelativePath,
                    metadata,
                    panelId),
            (AgentFileActionResult.Preview preview, FileAgentIntent.Read read) =>
                ProjectPreview(
                    preview.Value,
                    read.RelativePath,
                    metadata,
                    panelId),
            (
                AgentFileActionResult.AccessControl accessControl,
                FileAgentIntent.AccessRead accessRead) =>
                ProjectAccessControl(
                    accessControl,
                    accessRead,
                    metadata,
                    panelId),
            (
                AgentFileActionResult.Transfers transfers,
                FileAgentIntent.Transfers) =>
                ProjectTransfers(transfers, panelId),
            (
                AgentFileActionResult.CreatedDirectory created,
                FileAgentIntent.CreateDirectory createDirectory) =>
                ProjectCreatedDirectory(
                    created.Value,
                    createDirectory.RelativePath,
                    metadata,
                    panelId),
            (
                AgentFileActionResult.CreatedText created,
                FileAgentIntent.CreateText createText) =>
                ProjectTextWrite(
                    created.Value,
                    createText.RelativePath,
                    createText.Content,
                    replacedExisting: false,
                    metadata,
                    panelId),
            (
                AgentFileActionResult.ReplacedText replaced,
                FileAgentIntent.ReplaceText replaceText) =>
                ProjectTextWrite(
                    replaced.Value,
                    replaceText.RelativePath,
                    replaceText.Content,
                    replacedExisting: true,
                    metadata,
                    panelId),
            (
                AgentFileActionResult.Copied copied,
                FileAgentIntent.Copy copy) =>
                ProjectCopied(copied.Value, copy, metadata, panelId),
            (
                AgentFileActionResult.Moved moved,
                FileAgentIntent.Move move) =>
                ProjectMoved(
                    moved.Value,
                    move.DestinationRelativePath,
                    metadata,
                    panelId),
            (
                AgentFileActionResult.Deleted deleted,
                FileAgentIntent.Delete delete) =>
                ProjectDeleted(
                    deleted.Value,
                    delete.RelativePath,
                    metadata,
                    panelId),
            _ when IsMutation(intent) =>
                Rejected(FileMutationOutcomeUnknownStableCode, panelId),
            _ => Rejected("file_result_invalid", panelId),
        };
    }

    public static string Failure(
        HostError error,
        PanelInstanceId? panelId = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var stableCode = ProviderStableCode(error);
        return AgentToolResultJson.Failure(
            stableCode,
!string.Equals(stableCode, FileMutationOutcomeUnknownStableCode
, StringComparison.Ordinal) && error.Retryable,
            panelId);
    }

    public static string RejectedJson(
        string stableCode,
        PanelInstanceId? panelId = null) =>
        AgentToolResultJson.Failure(
            stableCode,
            retryable: false,
            panelId);

    internal static string ProviderStableCode(HostError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (string.Equals(
                error.StableCode,
                AgentActionFailureCodes.CompletionAuditUnavailable,
                StringComparison.Ordinal)
            || IsKnownProviderStableCode(error.StableCode))
        {
            return error.StableCode;
        }

        return error.Code switch
        {
            HostErrorCode.InvalidRequest => "invalid_request",
            HostErrorCode.NotFound => "not_found",
            HostErrorCode.RevisionConflict => "revision_conflict",
            HostErrorCode.UnsupportedProtocol => "unsupported_protocol",
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
            HostErrorCode.ResynchronizationRequired =>
                "resync_required",
            _ => "file_provider_failed",
        };
    }

    private static FileAgentToolJsonProjection ProjectPage(
        FilePanelPage page,
        ImmutableArray<FilePanelPathSegment> requestedPath,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        var maximumEntries = Math.Min(
            MaximumProviderEntries,
            page.Entries.Length);
        var projected = new List<ProjectedFileEntry>(maximumEntries);
        var projectionTruncated = false;
        for (var index = 0; index < maximumEntries; index++)
        {
            if (!TryProjectListEntry(
                page.Entries[index],
                requestedPath,
                metadata,
                out var entry))
            {
                return Rejected("file_result_invalid", panelId);
            }

            projected.Add(entry);
            projectionTruncated |= entry.Truncated;
        }

        var sourceTruncated =
            page.ContinuationToken is not null
            || page.Entries.Length > maximumEntries;
        for (var count = projected.Count; count >= 0; count--)
        {
            var resultTruncated =
                sourceTruncated
                || projectionTruncated
                || count < projected.Count;
            var serialized = SerializePage(
                projected,
                count,
                resultTruncated,
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

    private static FileAgentToolJsonProjection ProjectEntry(
        FilePanelEntry entry,
        AgentFileEntryReference? reference,
        ImmutableArray<FilePanelPathSegment> requestedPath,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        if (reference is null
            || !TryGetRelativePath(
                metadata.TrustedRoot,
                entry.Location,
                out var relativePath)
            || !relativePath.SequenceEqual(requestedPath))
        {
            return Rejected("file_result_invalid", panelId);
        }

        var projected = ProjectEntry(entry, relativePath);
        var serialized = SerializeEntry(projected, reference.Value, panelId);
        return serialized.ByteCount
            <= AgentKernelLimits.Default.MaximumToolResultBytes
                ? Succeeded(serialized.Json)
                : Rejected("file_limit_exceeded", panelId);
    }

    private static FileAgentToolJsonProjection ProjectPreview(
        FilePanelPreview preview,
        ImmutableArray<FilePanelPathSegment> requestedPath,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        if (preview.Kind is not (
                FilePanelPreviewKind.Text
                or FilePanelPreviewKind.StructuredText))
        {
            return Rejected("file_preview_not_text", panelId);
        }

        if (preview.Content.Length
            > AgentFileActionComposer.MaximumAgentReadBytes)
        {
            return Rejected("file_limit_exceeded", panelId);
        }

        if (!TryGetRelativePath(
                metadata.TrustedRoot,
                preview.Location,
                out var relativePath)
            || !relativePath.SequenceEqual(requestedPath))
        {
            return Rejected("file_result_invalid", panelId);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(preview.Content.Span);
        }
        catch (DecoderFallbackException)
        {
            return Rejected("file_content_invalid_utf8", panelId);
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var redacted = TerminalContentRedactor.Redact(text);
        var projectedPath = ProjectPath(relativePath);
        var sourceTruncated = preview.IsTruncated
            || projectedPath.Truncated;
        var full = SerializePreview(
            redacted.Text,
            PreviewKindName(preview.Kind),
            projectedPath,
            sourceTruncated,
            redacted.RedactionCount + projectedPath.Redactions,
            panelId);
        if (full.ByteCount
            <= AgentKernelLimits.Default.MaximumToolResultBytes)
        {
            return Succeeded(full.Json);
        }

        var runeEnds = RuneEndIndices(redacted.Text);
        string? best = null;
        var low = 0;
        var high = runeEnds.Count - 1;
        while (low <= high)
        {
            var candidateRunes = low + ((high - low) / 2);
            var candidateText = redacted.Text[..runeEnds[candidateRunes]];
            var candidate = SerializePreview(
                candidateText,
                PreviewKindName(preview.Kind),
                projectedPath,
                truncated: true,
                redacted.RedactionCount + projectedPath.Redactions,
                panelId);
            if (candidate.ByteCount
                <= AgentKernelLimits.Default.MaximumToolResultBytes)
            {
                best = candidate.Json;
                low = candidateRunes + 1;
            }
            else
            {
                high = candidateRunes - 1;
            }
        }

        return best is null
            ? Rejected("file_limit_exceeded", panelId)
            : Succeeded(best);
    }

    private static FileAgentToolJsonProjection ProjectCreatedDirectory(
        FilePanelEntry? entry,
        ImmutableArray<FilePanelPathSegment> requestedPath,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        if (entry is null
            || requestedPath.IsDefaultOrEmpty
            || entry.Kind != FilePanelEntryKind.Directory
            || !string.Equals(
                entry.Name,
                requestedPath[^1].Value,
                StringComparison.Ordinal)
            || !TryGetRelativePath(
                metadata.TrustedRoot,
                entry.Location,
                out var relativePath)
            || !relativePath.SequenceEqual(requestedPath))
        {
            return Rejected(
                FileMutationOutcomeUnknownStableCode,
                panelId);
        }

        return Succeeded(SerializeCreatedSuccess(panelId));
    }

    private static FileAgentToolJsonProjection ProjectTextWrite(
        FilePanelTextWriteReceipt receipt,
        ImmutableArray<FilePanelPathSegment> requestedPath,
        string content,
        bool replacedExisting,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        if (requestedPath.IsDefaultOrEmpty
            || receipt.ReplacedExisting != replacedExisting
            || receipt.BytesWritten != StrictUtf8.GetByteCount(content)
            || !TryGetRelativePath(
                metadata.TrustedRoot,
                receipt.Destination,
                out var relativePath)
            || !relativePath.SequenceEqual(requestedPath))
        {
            return Rejected(FileMutationOutcomeUnknownStableCode, panelId);
        }

        return Succeeded(SerializeMutationSuccess(
            replacedExisting ? "replaced" : "created",
            panelId));
    }

    private static FileAgentToolJsonProjection ProjectCopied(
        FilePanelCopyReceipt receipt,
        FileAgentIntent.Copy copy,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        if (receipt.BytesCopied < 0
            || receipt.BytesCopied > AgentFileActionComposer.MaximumAgentCopyBytes
            || !TryGetRelativePath(metadata.TrustedRoot, receipt.Source, out var source)
            || !source.SequenceEqual(copy.RelativePath)
            || !TryGetRelativePath(metadata.TrustedRoot, receipt.Destination, out var destination)
            || !destination.SequenceEqual(copy.DestinationRelativePath))
        {
            return Rejected(FileMutationOutcomeUnknownStableCode, panelId);
        }

        return Succeeded(SerializeMutationSuccess("copied", panelId));
    }

    private static FileAgentToolJsonProjection ProjectDeleted(
        FilePanelDeleteReceipt? receipt,
        ImmutableArray<FilePanelPathSegment> requestedPath,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        if (receipt is null
            || requestedPath.IsDefaultOrEmpty
            || !TryGetRelativePath(
                metadata.TrustedRoot,
                receipt.DeletedLocation,
                out var relativePath)
            || !relativePath.SequenceEqual(requestedPath))
        {
            return Rejected(
                FileMutationOutcomeUnknownStableCode,
                panelId);
        }

        return Succeeded(SerializeDeletedSuccess(panelId));
    }

    private static FileAgentToolJsonProjection ProjectMoved(
        FilePanelEntry? entry,
        ImmutableArray<FilePanelPathSegment> destinationPath,
        FileSessionMetadata metadata,
        PanelInstanceId? panelId)
    {
        if (entry is null
            || destinationPath.IsDefaultOrEmpty
            || !string.Equals(
                entry.Name,
                destinationPath[^1].Value,
                StringComparison.Ordinal)
            || !TryGetRelativePath(
                metadata.TrustedRoot,
                entry.Location,
                out var relativePath)
            || !relativePath.SequenceEqual(destinationPath))
        {
            return Rejected(
                FileMutationOutcomeUnknownStableCode,
                panelId);
        }

        return Succeeded(SerializeMovedSuccess(panelId));
    }

    private static bool TryProjectListEntry(
        FilePanelEntry entry,
        ImmutableArray<FilePanelPathSegment> requestedPath,
        FileSessionMetadata metadata,
        out ProjectedFileEntry projected)
    {
        projected = null!;
        if (!TryGetRelativePath(
                metadata.TrustedRoot,
                entry.Location,
                out var relativePath)
            || relativePath.Length != requestedPath.Length + 1
            || !relativePath.Take(requestedPath.Length)
                .SequenceEqual(requestedPath))
        {
            return false;
        }

        projected = ProjectEntry(entry, relativePath);
        return true;
    }

    private static ProjectedFileEntry ProjectEntry(
        FilePanelEntry entry,
        ImmutableArray<FilePanelPathSegment> relativePath)
    {
        var path = ProjectPath(relativePath);
        var projectedName = ProjectText(
            relativePath.IsEmpty
                ? "."
                : relativePath[^1].Value,
            MaximumProviderMetadataBytes);
        return new ProjectedFileEntry(
            path,
            projectedName.Text,
            EntryKindName(entry.Kind),
            entry.Size,
            entry.LastModifiedAt,
            entry.IsHidden,
            path.Truncated || projectedName.Truncated,
            checked(path.Redactions + projectedName.Redactions));
    }

    private static ProjectedPath ProjectPath(
        ImmutableArray<FilePanelPathSegment> path)
    {
        var segments = ImmutableArray.CreateBuilder<string>(path.Length);
        var truncated = false;
        var redactions = 0;
        foreach (var segment in path)
        {
            var projected = ProjectText(
                segment.Value,
                FileAgentToolSet.MaximumPathSegmentBytes);
            segments.Add(projected.Text);
            truncated |= projected.Truncated;
            redactions = checked(redactions + projected.Redactions);
        }

        return new ProjectedPath(
            segments.ToImmutable(),
            truncated,
            redactions);
    }

    private static ProjectedText ProjectText(
        string value,
        int maximumBytes)
    {
        var redacted = TerminalContentRedactor.Redact(value);
        var text = TruncateUtf8(
            redacted.Text,
            maximumBytes,
            out var truncated);
        return new ProjectedText(
            text,
            truncated,
            redacted.RedactionCount);
    }

    private static bool TryGetRelativePath(
        FilePanelLocation trustedRoot,
        FilePanelLocation candidate,
        out ImmutableArray<FilePanelPathSegment> relativePath)
    {
        relativePath = [];
        if (!string.Equals(
                trustedRoot.ProviderProfileId,
                candidate.ProviderProfileId,
                StringComparison.Ordinal)
            || !string.Equals(
                trustedRoot.Authority,
                candidate.Authority,
                StringComparison.Ordinal)
            || trustedRoot.Version is not null
            || trustedRoot.Address
                is not FilePanelAddress.Hierarchical rootAddress
            || candidate.Address
                is not FilePanelAddress.Hierarchical candidateAddress)
        {
            return false;
        }

        var root = rootAddress.Path.Segments;
        var path = candidateAddress.Path.Segments;
        if (path.Length < root.Length
            || path.Length - root.Length
                > FileAgentToolSet.MaximumPathSegments
            || !path.Take(root.Length).SequenceEqual(root))
        {
            return false;
        }

        relativePath = path[root.Length..];
        var totalBytes = 0;
        foreach (var segment in relativePath)
        {
            int segmentBytes;
            try
            {
                segmentBytes = StrictUtf8.GetByteCount(segment.Value);
            }
            catch (EncoderFallbackException)
            {
                return false;
            }

            if (segmentBytes > FileAgentToolSet.MaximumPathSegmentBytes)
            {
                return false;
            }

            totalBytes = checked(
                totalBytes
                + segmentBytes
                + (totalBytes == 0 ? 0 : 1));
            if (totalBytes > FileAgentToolSet.MaximumRelativePathBytes)
            {
                return false;
            }
        }

        return true;
    }

    private static (string Json, int ByteCount) SerializePage(
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
        writer.WritePropertyName("entries");
        writer.WriteStartArray();
        for (var index = 0; index < count; index++)
        {
            WriteEntry(writer, entries[index]);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return (
            Encoding.UTF8.GetString(buffer.WrittenSpan),
            buffer.WrittenCount);
    }

    private static (string Json, int ByteCount) SerializeEntry(
        ProjectedFileEntry entry,
        AgentFileEntryReference reference,
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteString("content_origin", "untrusted_file");
        writer.WriteBoolean("truncated", entry.Truncated);
        writer.WriteNumber("redactions", entry.Redactions);
        writer.WriteString("entry_ref", reference.Value);
        writer.WritePropertyName("entry");
        WriteEntry(writer, entry);
        writer.WriteEndObject();
        writer.Flush();
        return (
            Encoding.UTF8.GetString(buffer.WrittenSpan),
            buffer.WrittenCount);
    }

    private static (string Json, int ByteCount) SerializePreview(
        string text,
        string previewKind,
        ProjectedPath path,
        bool truncated,
        int redactions,
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteString("content_origin", "untrusted_file");
        WritePath(writer, path.Segments);
        writer.WriteString("preview_kind", previewKind);
        writer.WriteBoolean("truncated", truncated);
        writer.WriteNumber("redactions", redactions);
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.Flush();
        return (
            Encoding.UTF8.GetString(buffer.WrittenSpan),
            buffer.WrittenCount);
    }

    private static string SerializeCreatedSuccess(
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteBoolean("created", true);

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string SerializeDeletedSuccess(
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteBoolean("deleted", true);
        writer.WriteBoolean("permanent", true);

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string SerializeMovedSuccess(
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteBoolean("moved", true);
        writer.WriteBoolean("destination_created", true);

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string SerializeMutationSuccess(
        string effect,
        PanelInstanceId? panelId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSuccessStart(writer, panelId);
        writer.WriteString("effect", effect);
        writer.WriteBoolean("completed", true);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSuccessStart(
        Utf8JsonWriter writer,
        PanelInstanceId? panelId)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("ok", true);
        AgentToolResultJson.WritePanelId(writer, panelId);
    }

    private static void WriteEntry(
        Utf8JsonWriter writer,
        ProjectedFileEntry entry)
    {
        writer.WriteStartObject();
        WritePath(writer, entry.Path.Segments);
        writer.WriteString("name", entry.Name);
        writer.WriteString("kind", entry.Kind);
        if (entry.Size is { } size)
        {
            writer.WriteNumber("size", size);
        }

        if (entry.LastModifiedAt is { } modified)
        {
            writer.WriteString(
                "last_modified_at_utc",
                modified.ToUniversalTime());
        }

        writer.WriteBoolean("hidden", entry.IsHidden);
        writer.WriteEndObject();
    }

    private static void WritePath(
        Utf8JsonWriter writer,
        ImmutableArray<string> segments)
    {
        writer.WritePropertyName("path_segments");
        writer.WriteStartArray();
        foreach (var segment in segments)
        {
            writer.WriteStringValue(segment);
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
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }

            builder.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }

        truncated = true;
        return builder.ToString();
    }

    private static List<int> RuneEndIndices(string value)
    {
        var ends = new List<int>(value.Length + 1)
        {
            0,
        };
        for (var index = 0; index < value.Length;)
        {
            var length = char.IsHighSurrogate(value[index])
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1])
                    ? 2
                    : 1;
            index += length;
            ends.Add(index);
        }

        return ends;
    }

    private static string EntryKindName(FilePanelEntryKind kind) =>
        kind switch
        {
            FilePanelEntryKind.File => "file",
            FilePanelEntryKind.Directory => "directory",
            FilePanelEntryKind.Link => "link",
            FilePanelEntryKind.Other => "other",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                null),
        };

    private static string PreviewKindName(FilePanelPreviewKind kind) =>
        kind switch
        {
            FilePanelPreviewKind.Text => "text",
            FilePanelPreviewKind.StructuredText => "structured_text",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                null),
        };

    private static bool IsMutation(FileAgentIntent intent) =>
        intent is FileAgentIntent.CreateDirectory
            or FileAgentIntent.CreateText
            or FileAgentIntent.ReplaceText
            or FileAgentIntent.Copy
            or FileAgentIntent.Move
            or FileAgentIntent.Delete;

    private static bool IsKnownProviderStableCode(string stableCode) =>
        stableCode is
            "file_result_invalid"
            or "file_reference_invalid"
            or FileMutationOutcomeUnknownStableCode
            or "file_preview_not_text"
            or "file_content_sensitive"
            or "file_not_found"
            or "file_access_denied"
            or "file_location_invalid"
            or "file_name_invalid"
            or "file_outside_root"
            or "file_root_mutation_not_allowed"
            or "file_already_exists"
            or "file_conflict"
            or "file_precondition_failed"
            or "file_not_directory"
            or "file_is_directory"
            or "file_directory_not_empty"
            or "file_link_not_allowed"
            or "file_quota_exceeded"
            or "file_capability_not_supported"
            or "file_limit_exceeded"
            or "file_provider_offline"
            or "file_authentication_required"
            or "file_provider_failed"
            or "file_operation_rejected";

    private static FileAgentToolJsonProjection Succeeded(string json) =>
        new(true, "tool_succeeded", json);

    private static FileAgentToolJsonProjection Rejected(
        string stableCode,
        PanelInstanceId? panelId) =>
        new(
            false,
            stableCode,
            RejectedJson(stableCode, panelId));

    private sealed record ProjectedText(
        string Text,
        bool Truncated,
        int Redactions);

    private sealed record ProjectedPath(
        ImmutableArray<string> Segments,
        bool Truncated,
        int Redactions);

    private sealed record ProjectedFileEntry(
        ProjectedPath Path,
        string Name,
        string Kind,
        long? Size,
        DateTimeOffset? LastModifiedAt,
        bool IsHidden,
        bool Truncated,
        int Redactions);
}

internal sealed record FileAgentToolJsonProjection(
    bool IsSuccess,
    string StableCode,
    string Json);

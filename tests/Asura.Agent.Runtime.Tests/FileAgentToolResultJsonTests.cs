using System.Text;
using System.Text.Json;
using Asura.Agent;
using Asura.Agent.Runtime;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Runtime.Tests;

public sealed class FileAgentToolResultJsonTests
{
    [Fact]
    public void ListResultIsRelativeBoundedRedactedAndStripsOpaqueMaterial()
    {
        var metadata = Metadata();
        var requestedPath = Segments("logs");
        var secretName = """{"password":"secret-canary"}""";
        var entry = new FilePanelEntry(
            Location(
                metadata,
                "logs",
                secretName,
                version: "opaque-version-secret"),
            secretName,
            FilePanelEntryKind.File,
            Size: 42,
            LastModifiedAt: DateTimeOffset.UnixEpoch,
            IsHidden: true);
        var result = new AgentFileActionResult.Page(
            new FilePanelPage(
                [entry],
                continuationToken: "opaque-continuation-secret"));

        var projection = FileAgentToolResultJson.Project(
            result,
            new FileAgentIntent.List(requestedPath),
            metadata,
            new PanelInstanceId("files-panel"));

        Assert.True(projection.IsSuccess);
        Assert.Equal("tool_succeeded", projection.StableCode);
        Assert.DoesNotContain(
            "secret-canary",
            projection.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "opaque-continuation-secret",
            projection.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "opaque-version-secret",
            projection.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            metadata.TrustedRoot.ProviderProfileId,
            projection.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            metadata.TrustedRoot.Authority!,
            projection.Json,
            StringComparison.Ordinal);

        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(
            "files-panel",
            root.GetProperty("panel_id").GetString());
        Assert.Equal(
            "untrusted_file",
            root.GetProperty("content_origin").GetString());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, root.GetProperty("redactions").GetInt32());
        var projectedEntry = Assert.Single(
            root.GetProperty("entries").EnumerateArray());
        Assert.Equal(
            ["logs", "[REDACTED SECRET-BEARING LINE]"],
            projectedEntry.GetProperty("path_segments")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            "[REDACTED SECRET-BEARING LINE]",
            projectedEntry.GetProperty("name").GetString());
        Assert.Equal("file", projectedEntry.GetProperty("kind").GetString());
        Assert.Equal(42, projectedEntry.GetProperty("size").GetInt64());
        Assert.True(projectedEntry.GetProperty("hidden").GetBoolean());
    }

    [Fact]
    public void StatResultExposesOnlySafeRelativeMetadata()
    {
        var metadata = Metadata();
        var requestedPath = Segments("reports", "status.txt");
        var entry = new FilePanelEntry(
            Location(metadata, "reports", "status.txt"),
            "provider-controlled display name",
            FilePanelEntryKind.File,
            Size: 123,
            LastModifiedAt: null,
            IsHidden: false);

        var projection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Entry(entry, Reference()),
            new FileAgentIntent.Stat(requestedPath),
            metadata);

        Assert.True(projection.IsSuccess);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        Assert.Equal(
            "untrusted_file",
            root.GetProperty("content_origin").GetString());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(0, root.GetProperty("redactions").GetInt32());
        Assert.Equal(Reference().Value, root.GetProperty("entry_ref").GetString());
        var projectedEntry = root.GetProperty("entry");
        Assert.Equal(
            ["reports", "status.txt"],
            projectedEntry.GetProperty("path_segments")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        Assert.Equal(
            "status.txt",
            projectedEntry.GetProperty("name").GetString());
        Assert.False(projectedEntry.TryGetProperty("version", out _));
        Assert.False(projectedEntry.TryGetProperty("location", out _));
        Assert.False(projectedEntry.TryGetProperty("profile", out _));
    }

    [Fact]
    public void TextPreviewUsesStrictUtf8AndRedactsSecretBearingLines()
    {
        var metadata = Metadata();
        var requestedPath = Segments("config.json");
        var content = Encoding.UTF8.GetBytes(
            """
            service=operations
            {"token":"secret-canary"}
            mode=read-only
            """);
        var preview = new FilePanelPreview(
            Location(metadata, "config.json"),
            FilePanelPreviewKind.StructuredText,
            "application/json",
            content,
            isTruncated: false);

        var projection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Preview(preview),
            new FileAgentIntent.Read(requestedPath),
            metadata);

        Assert.True(projection.IsSuccess);
        Assert.DoesNotContain(
            "secret-canary",
            projection.Json,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(projection.Json);
        var root = document.RootElement;
        Assert.Equal(
            "untrusted_file",
            root.GetProperty("content_origin").GetString());
        Assert.Equal(
            "structured_text",
            root.GetProperty("preview_kind").GetString());
        Assert.Equal(1, root.GetProperty("redactions").GetInt32());
        Assert.Contains(
            "[REDACTED SECRET-BEARING LINE]",
            root.GetProperty("text").GetString(),
            StringComparison.Ordinal);
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.False(root.TryGetProperty("media_type", out _));
    }

    [Fact]
    public void InvalidUtf8AndNonTextPreviewsFailClosed()
    {
        var metadata = Metadata();
        var path = Segments("artifact.bin");
        var invalidUtf8 = new FilePanelPreview(
            Location(metadata, "artifact.bin"),
            FilePanelPreviewKind.Text,
            "text/plain",
            [0xC3, 0x28],
            isTruncated: false);
        var image = new FilePanelPreview(
            Location(metadata, "artifact.bin"),
            FilePanelPreviewKind.Image,
            "image/png",
            [1, 2, 3],
            isTruncated: false);

        var invalidProjection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Preview(invalidUtf8),
            new FileAgentIntent.Read(path),
            metadata);
        var imageProjection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Preview(image),
            new FileAgentIntent.Read(path),
            metadata);

        Assert.False(invalidProjection.IsSuccess);
        Assert.Equal(
            "file_content_invalid_utf8",
            invalidProjection.StableCode);
        Assert.False(imageProjection.IsSuccess);
        Assert.Equal(
            "file_preview_not_text",
            imageProjection.StableCode);
        Assert.DoesNotContain(
            Convert.ToBase64String(invalidUtf8.Content.Span),
            invalidProjection.Json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewProjectionStaysWithinKernelLimitAfterJsonEscaping()
    {
        var metadata = Metadata();
        var path = Segments("large.txt");
        var content = Encoding.UTF8.GetBytes(
            string.Concat(
                Enumerable.Repeat(
                    "\\\"\t",
                    checked((int)
                        (AgentFileActionComposer.MaximumAgentReadBytes / 3)))));
        var preview = new FilePanelPreview(
            Location(metadata, "large.txt"),
            FilePanelPreviewKind.Text,
            "text/plain",
            content,
            isTruncated: false);

        var projection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Preview(preview),
            new FileAgentIntent.Read(path),
            metadata);

        Assert.True(projection.IsSuccess);
        Assert.True(
            Encoding.UTF8.GetByteCount(projection.Json)
            <= AgentKernelLimits.Default.MaximumToolResultBytes);
        using var document = JsonDocument.Parse(projection.Json);
        Assert.True(
            document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.DoesNotContain(
            '\uFFFD',
            document.RootElement.GetProperty("text").GetString()!);
    }

    [Fact]
    public void ProviderLocationsOutsideThePinnedRootAreRejected()
    {
        var metadata = Metadata();
        var outside = new FilePanelLocation(
            "different-profile",
            authority: null,
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(
                [new FilePanelPathSegment("escape.txt")])));
        var entry = new FilePanelEntry(
            outside,
            "escape.txt",
            FilePanelEntryKind.File,
            Size: null,
            LastModifiedAt: null,
            IsHidden: false);

        var projection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Entry(entry, Reference()),
            new FileAgentIntent.Stat(Segments("escape.txt")),
            metadata);

        Assert.False(projection.IsSuccess);
        Assert.Equal(
            "file_result_invalid",
            projection.StableCode);
        Assert.DoesNotContain(
            "different-profile",
            projection.Json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MutationSuccessReceiptsAreFixedAndStripProviderMetadata()
    {
        var metadata = Metadata();
        var createPath = Segments("deploy", "current");
        var created = new FilePanelEntry(
            Location(
                metadata,
                "deploy",
                "current",
                version: "opaque-created-version"),
            "current",
            FilePanelEntryKind.Directory,
            Size: 999,
            LastModifiedAt: DateTimeOffset.UnixEpoch,
            IsHidden: true);
        var deletePath = Segments("deploy", "obsolete");
        var moveSourcePath = Segments("deploy", "draft.txt");
        var moveDestinationPath = Segments("archive", "report.txt");
        var moved = new FilePanelEntry(
            Location(
                metadata,
                "archive",
                "report.txt",
                version: "opaque-moved-version"),
            "report.txt",
            FilePanelEntryKind.File,
            Size: 111,
            LastModifiedAt: DateTimeOffset.UnixEpoch,
            IsHidden: true);
        var deleted = new FilePanelDeleteReceipt(
            Location(
                metadata,
                "deploy",
                "obsolete",
                version: "opaque-deleted-version"),
            WasDirectory: true);

        var createdProjection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.CreatedDirectory(created),
            new FileAgentIntent.CreateDirectory(createPath),
            metadata);
        var deletedProjection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Deleted(deleted),
            new FileAgentIntent.Delete(deletePath, Reference()),
            metadata,
            new PanelInstanceId("files-panel"));
        var movedProjection = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Moved(moved),
            new FileAgentIntent.Move(moveSourcePath, Reference(), moveDestinationPath),
            metadata,
            new PanelInstanceId("files-panel"));

        Assert.True(createdProjection.IsSuccess);
        Assert.Equal(
            """{"ok":true,"created":true}""",
            createdProjection.Json);
        Assert.True(deletedProjection.IsSuccess);
        Assert.Equal(
            """
            {"ok":true,"panel_id":"files-panel","deleted":true,"permanent":true}
            """,
            deletedProjection.Json);
        Assert.True(movedProjection.IsSuccess);
        Assert.Equal(
            """
            {"ok":true,"panel_id":"files-panel","moved":true,"destination_created":true}
            """,
            movedProjection.Json);
        foreach (var sensitive in new[]
        {
            metadata.TrustedRoot.ProviderProfileId,
            metadata.TrustedRoot.Authority!,
            "deploy",
            "current",
            "obsolete",
            "report.txt",
            "opaque-moved-version",
            "opaque-created-version",
            "opaque-deleted-version",
        })
        {
            Assert.DoesNotContain(
                sensitive,
                createdProjection.Json + deletedProjection.Json,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SearchAccessAndTransferObservationsAreBoundedAndSanitized()
    {
        var metadata = Metadata();
        var searchEntry = new FilePanelEntry(
            Location(metadata, "logs", "error.log"),
            "error.log",
            FilePanelEntryKind.File,
            5,
            null,
            false);
        var search = FileAgentToolResultJson.Project(
            new AgentFileActionResult.SearchResults([searchEntry], false),
            new FileAgentIntent.Search(
                Segments("logs"),
                "error",
                FilePanelDiscoveryScope.Subtree,
                10),
            metadata);
        var access = FileAgentToolResultJson.Project(
            new AgentFileActionResult.AccessControl(
                new FilePanelAccessControl(
                    Location(metadata, "report.txt"),
                    new FilePanelPosixMode(0x1A4),
                    owner: "alice",
                    group: "staff",
                    grants:
                    [
                        new FilePanelAccessGrant(
                            new FilePanelGrantee(
                                FilePanelGranteeKind.User,
                                "user-1",
                                "Alice"),
                            FilePanelAccessRight.Read
                            | FilePanelAccessRight.ReadAcl),
                    ])),
            new FileAgentIntent.AccessRead(Segments("report.txt")),
            metadata);
        var transfer = new FilePanelTransferSnapshot(
            FilePanelTransferId.New(),
            new FilePanelTransferRequest(
                Location(metadata, "source.txt"),
                new FilePanelLocation(
                    "external-provider-canary",
                    "external-authority-canary",
                    new FilePanelAddress.ObjectKey(
                        "external/destination-canary")),
                FilePanelTransferOperation.Copy,
                FilePanelConflictPolicy.Fail),
            new FilePanelLocation(
                "external-provider-canary",
                "external-authority-canary",
                new FilePanelAddress.ObjectKey(
                    "external/effective-canary")),
            FilePanelTransferState.Running,
            "Copying",
            12,
            24,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null);
        var transfers = FileAgentToolResultJson.Project(
            new AgentFileActionResult.Transfers([transfer], false),
            new FileAgentIntent.Transfers(),
            metadata);

        Assert.All([search, access, transfers], projection =>
        {
            Assert.True(projection.IsSuccess);
            Assert.True(
                Encoding.UTF8.GetByteCount(projection.Json)
                <= AgentKernelLimits.Default.MaximumToolResultBytes);
        });
        using var searchDocument = JsonDocument.Parse(search.Json);
        Assert.Equal("error.log", Assert.Single(
            searchDocument.RootElement.GetProperty("matches")
                .EnumerateArray()).GetProperty("name").GetString());
        using var accessDocument = JsonDocument.Parse(access.Json);
        Assert.Equal(
            "644",
            accessDocument.RootElement.GetProperty("mode_octal").GetString());
        Assert.Equal(
            ["read", "read_acl"],
            Assert.Single(accessDocument.RootElement.GetProperty("grants")
                    .EnumerateArray())
                .GetProperty("rights")
                .EnumerateArray()
                .Select(value => value.GetString()), StringComparer.Ordinal);
        using var transferDocument = JsonDocument.Parse(transfers.Json);
        var transferRoot = transferDocument.RootElement;
        Assert.True(transferRoot.GetProperty(
            "cancellation_does_not_rollback_bytes").GetBoolean());
        var projectedTransfer = Assert.Single(
            transferRoot.GetProperty("transfers").EnumerateArray());
        Assert.False(projectedTransfer.GetProperty(
            "governed_cancel_available").GetBoolean());
        Assert.False(projectedTransfer.GetProperty(
            "governed_retry_available").GetBoolean());
        Assert.DoesNotContain(
            metadata.TrustedRoot.ProviderProfileId,
            transfers.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            metadata.TrustedRoot.Authority!,
            transfers.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain("source.txt", transfers.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("external-provider-canary", transfers.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("external-authority-canary", transfers.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("destination-canary", transfers.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("effective-canary", transfers.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidMutationReceiptsAreNonRetryableOutcomeUnknown()
    {
        var metadata = Metadata();
        var path = Segments("deploy", "current");
        var wrongKind = new FilePanelEntry(
            Location(metadata, "deploy", "current"),
            "current",
            FilePanelEntryKind.File,
            Size: null,
            LastModifiedAt: null,
            IsHidden: false);
        var wrongName = new FilePanelEntry(
            Location(metadata, "deploy", "current"),
            "provider-alias",
            FilePanelEntryKind.Directory,
            Size: null,
            LastModifiedAt: null,
            IsHidden: false);
        var outside = new FilePanelDeleteReceipt(
            new FilePanelLocation(
                "other-provider",
                authority: null,
                new FilePanelAddress.Hierarchical(
                    FilePanelPath.FromSegments(
                        [new FilePanelPathSegment("current")]))),
            WasDirectory: false);

        var projections = new[]
        {
            FileAgentToolResultJson.Project(
                new AgentFileActionResult.CreatedDirectory(wrongKind),
                new FileAgentIntent.CreateDirectory(path),
                metadata),
            FileAgentToolResultJson.Project(
                new AgentFileActionResult.CreatedDirectory(wrongName),
                new FileAgentIntent.CreateDirectory(path),
                metadata),
            FileAgentToolResultJson.Project(
                new AgentFileActionResult.Deleted(outside),
                new FileAgentIntent.Delete(path, Reference()),
                metadata),
            FileAgentToolResultJson.Project(
                new AgentFileActionResult.Entry(wrongKind),
                new FileAgentIntent.CreateDirectory(path),
                metadata),
        };

        Assert.All(projections, projection =>
        {
            Assert.False(projection.IsSuccess);
            Assert.Equal(
                FileAgentToolResultJson
                    .FileMutationOutcomeUnknownStableCode,
                projection.StableCode);
            using var document = JsonDocument.Parse(projection.Json);
            var error = document.RootElement.GetProperty("error");
            Assert.Equal(
                FileAgentToolResultJson
                    .FileMutationOutcomeUnknownStableCode,
                error.GetProperty("code").GetString());
            Assert.False(error.GetProperty("retryable").GetBoolean());
        });
    }

    [Fact]
    public void HostMutationOutcomeUnknownCannotBecomeRetryable()
    {
        var error = new HostError(
            HostErrorCode.EngineFailed,
            FileAgentToolResultJson.FileMutationOutcomeUnknownStableCode,
            "provider response was lost",
            Retryable: true);

        var json = FileAgentToolResultJson.Failure(error);

        Assert.Equal(
            FileAgentToolResultJson.FileMutationOutcomeUnknownStableCode,
            FileAgentToolResultJson.ProviderStableCode(error));
        using var document = JsonDocument.Parse(json);
        var projected = document.RootElement.GetProperty("error");
        Assert.Equal(
            FileAgentToolResultJson.FileMutationOutcomeUnknownStableCode,
            projected.GetProperty("code").GetString());
        Assert.False(projected.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain(
            "provider response",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HostFailuresExposeOnlyClosedStableCodes()
    {
        var error = new HostError(
            HostErrorCode.EngineFailed,
            "provider-secret-internal-code",
            "provider-secret-internal-message",
            Retryable: true);

        var json = FileAgentToolResultJson.Failure(error);

        Assert.DoesNotContain(
            "provider-secret",
            json,
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var projected = document.RootElement.GetProperty("error");
        Assert.Equal(
            "file_provider_failed",
            projected.GetProperty("code").GetString());
        Assert.True(projected.GetProperty("retryable").GetBoolean());
        Assert.False(projected.TryGetProperty("message", out _));
    }

    [Fact]
    public void CompletionAuditFailureRetainsTheRunQuarantineCode()
    {
        var error = new HostError(
            HostErrorCode.EngineFailed,
            AgentActionFailureCodes.CompletionAuditUnavailable,
            "internal audit detail");

        Assert.Equal(
            AgentActionFailureCodes.CompletionAuditUnavailable,
            FileAgentToolResultJson.ProviderStableCode(error));
        Assert.DoesNotContain(
            "internal audit detail",
            FileAgentToolResultJson.Failure(error),
            StringComparison.Ordinal);
    }

    private static FileSessionMetadata Metadata()
    {
        var root = new FilePanelLocation(
            "production-files",
            "opaque-authority",
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(
                [
                    new FilePanelPathSegment("srv"),
                    new FilePanelPathSegment("operations"),
                ])));
        return new FileSessionMetadata(
            root,
            FilePanelCapability.List
            | FilePanelCapability.Stat
            | FilePanelCapability.RangedRead,
            maximumListPageSize: 100,
            maximumPreviewBytes: 64 * 1024);
    }

    private static FilePanelLocation Location(
        FileSessionMetadata metadata,
        string first,
        string? second = null,
        string? version = null)
    {
        var segments = ((FilePanelAddress.Hierarchical)
                metadata.TrustedRoot.Address)
            .Path.Segments
            .Add(new FilePanelPathSegment(first));
        if (second is not null)
        {
            segments = segments.Add(new FilePanelPathSegment(second));
        }

        return new FilePanelLocation(
            metadata.TrustedRoot.ProviderProfileId,
            metadata.TrustedRoot.Authority,
            new FilePanelAddress.Hierarchical(
                FilePanelPath.FromSegments(segments)),
            version);
    }

    private static FilePanelPathSegment[] Segments(params string[] values) =>
        [.. values.Select(value => new FilePanelPathSegment(value))];

    private static AgentFileEntryReference Reference() =>
        new(new string('A', AgentFileEntryReference.EncodedLength));
}

using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Builds a canonical ZIP from an explicit, text-only request. Validation and redaction complete in
/// memory before the destination is touched; this type intentionally has no access to runtime state.
/// </summary>
public sealed class DeterministicDiagnosticsBundleExporter : IDiagnosticsBundleExporter
{
    private const int ManifestSchemaVersion = 2;
    private const int MaximumPathSegmentLength = 80;
    private const int MaximumPathSegmentCount = 12;
    private const string ManifestPath = "manifest.json";
    private const string ArtifactPathPrefix = "artifacts/";
    private static readonly DateTimeOffset ZipEpoch =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] ForbiddenPathFragments =
    [
        "terminal",
        "transcript",
        "scrollback",
        "command-history",
        "command_text",
        "command-text",
        "credentials",
        "credential-dump",
        "environment",
        "env-dump",
        "secrets",
        "private-key",
        "shell-history",
        "stdin",
        "stdout",
        "stderr",
    ];

    public async ValueTask<DiagnosticsBundleResult<DiagnosticsBundleReceipt>> ExportAsync(
        DiagnosticsBundleRequest request,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);

        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(DiagnosticsBundleErrorCode.Cancelled, "Diagnostics export was cancelled.");
        }

        if (!CanWrite(destination))
        {
            return Failure(
                DiagnosticsBundleErrorCode.DestinationUnavailable,
                "The diagnostics destination is not writable.");
        }

        var preparedRequest = Prepare(request, cancellationToken);
        if (!preparedRequest.IsSuccess)
        {
            return DiagnosticsBundleResult<DiagnosticsBundleReceipt>.Failure(
                preparedRequest.Error!);
        }

        byte[] archiveBytes;
        try
        {
            archiveBytes = BuildArchive(preparedRequest.Value!, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(DiagnosticsBundleErrorCode.Cancelled, "Diagnostics export was cancelled.");
        }

        if (archiveBytes.LongLength > DiagnosticsBundleLimits.MaximumArchiveBytes)
        {
            return Failure(
                DiagnosticsBundleErrorCode.BundleTooLarge,
                "The diagnostics archive exceeds the export size limit.");
        }

        try
        {
            await destination.WriteAsync(archiveBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(DiagnosticsBundleErrorCode.Cancelled, "Diagnostics export was cancelled.");
        }
        catch (Exception exception) when (IsDestinationFailure(exception))
        {
            return Failure(
                DiagnosticsBundleErrorCode.DestinationUnavailable,
                "The diagnostics archive could not be written.");
        }

        var prepared = preparedRequest.Value!;
        var receipt = new DiagnosticsBundleReceipt(
            prepared.Artifacts.Count,
            prepared.TotalArtifactBytes,
            archiveBytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(archiveBytes)));
        return DiagnosticsBundleResult<DiagnosticsBundleReceipt>.Success(receipt);
    }

    // Request preparation owns every validation step so archive construction receives only canonical,
    // already-scanned values and cannot accidentally write a partially validated bundle.

    private static DiagnosticsBundleResult<PreparedRequest> Prepare(
        DiagnosticsBundleRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Metadata is null || request.Artifacts is null)
        {
            return InvalidRequest("The diagnostics request is incomplete.");
        }

        var metadataResult = PrepareMetadata(request.Metadata);
        if (!metadataResult.IsSuccess)
        {
            return DiagnosticsBundleResult<PreparedRequest>.Failure(metadataResult.Error!);
        }

        if (request.Artifacts.Count > DiagnosticsBundleLimits.MaximumArtifactCount)
        {
            return DiagnosticsBundleResult<PreparedRequest>.Failure(new DiagnosticsBundleError(
                DiagnosticsBundleErrorCode.TooManyArtifacts,
                "The diagnostics request contains too many artifacts."));
        }

        var artifacts = new List<PreparedArtifact>(request.Artifacts.Count);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalInputBytes = 0;
        long totalExportedBytes = 0;
        for (var index = 0; index < request.Artifacts.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return DiagnosticsBundleResult<PreparedRequest>.Failure(
                    new DiagnosticsBundleError(
                        DiagnosticsBundleErrorCode.Cancelled,
                        "Diagnostics export was cancelled."));
            }

            var artifact = request.Artifacts[index];
            var artifactResult = PrepareArtifact(artifact, index);
            if (!artifactResult.IsSuccess)
            {
                return DiagnosticsBundleResult<PreparedRequest>.Failure(artifactResult.Error!);
            }

            var prepared = artifactResult.Value!;
            if (!paths.Add(prepared.Path))
            {
                return DiagnosticsBundleResult<PreparedRequest>.Failure(new DiagnosticsBundleError(
                    DiagnosticsBundleErrorCode.DuplicatePath,
                    "The diagnostics request contains duplicate artifact paths.",
                    index));
            }

            totalInputBytes += prepared.InputByteLength;
            totalExportedBytes += prepared.Content.LongLength;
            if (totalInputBytes > DiagnosticsBundleLimits.MaximumTotalArtifactBytes
                || totalExportedBytes > DiagnosticsBundleLimits.MaximumTotalArtifactBytes)
            {
                return DiagnosticsBundleResult<PreparedRequest>.Failure(new DiagnosticsBundleError(
                    DiagnosticsBundleErrorCode.BundleTooLarge,
                    "The diagnostics artifacts exceed the total size limit.",
                    index));
            }

            artifacts.Add(prepared);
        }

        artifacts.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        return DiagnosticsBundleResult<PreparedRequest>.Success(new PreparedRequest(
            metadataResult.Value!,
            artifacts.AsReadOnly(),
            totalExportedBytes));
    }

    private static DiagnosticsBundleResult<PreparedMetadata> PrepareMetadata(
        DiagnosticsBundleMetadata metadata)
    {
        var values = new[]
        {
            metadata.ApplicationName,
            metadata.ApplicationIdentifier,
            metadata.ExecutableName,
            metadata.ApplicationVersion,
            metadata.RuntimeVersion,
            metadata.OperatingSystem,
            metadata.Architecture,
        };
        var normalized = new string[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var failure = DiagnosticsContentSafety.TryNormalizeMetadata(
                values[index],
                out normalized[index]);
            if (failure == DiagnosticsSafetyFailure.UnsafeContent)
            {
                return DiagnosticsBundleResult<PreparedMetadata>.Failure(
                    new DiagnosticsBundleError(
                        DiagnosticsBundleErrorCode.UnsafeContent,
                        "The diagnostics metadata did not pass the safety scan."));
            }

            if (failure != DiagnosticsSafetyFailure.None
                || !DiagnosticsContentSafety.TryEncodeUtf8(normalized[index], out var bytes)
                || bytes.Length > DiagnosticsBundleLimits.MaximumMetadataValueBytes)
            {
                return DiagnosticsBundleResult<PreparedMetadata>.Failure(
                    new DiagnosticsBundleError(
                        DiagnosticsBundleErrorCode.InvalidRequest,
                        "The diagnostics metadata is invalid or exceeds its size limit."));
            }
        }

        return DiagnosticsBundleResult<PreparedMetadata>.Success(new PreparedMetadata(
            normalized[0],
            normalized[1],
            normalized[2],
            normalized[3],
            normalized[4],
            normalized[5],
            normalized[6],
            metadata.CapturedAt.ToUniversalTime()));
    }

    private static DiagnosticsBundleResult<PreparedArtifact> PrepareArtifact(
        DiagnosticsBundleArtifact? artifact,
        int index)
    {
        if (artifact is null || !Enum.IsDefined(artifact.Kind))
        {
            return InvalidArtifact(
                DiagnosticsBundleErrorCode.InvalidRequest,
                "A diagnostics artifact has invalid metadata.",
                index);
        }

        if (!TryNormalizePath(artifact.RelativePath, artifact.Kind, out var normalizedPath))
        {
            return InvalidArtifact(
                DiagnosticsBundleErrorCode.InvalidPath,
                "A diagnostics artifact path is not safe and portable.",
                index);
        }

        if (!DiagnosticsContentSafety.TryEncodeUtf8(artifact.Content, out var inputBytes))
        {
            return InvalidArtifact(
                DiagnosticsBundleErrorCode.InvalidRequest,
                "A diagnostics artifact is not valid text.",
                index);
        }

        if (inputBytes.Length > DiagnosticsBundleLimits.MaximumArtifactBytes)
        {
            return InvalidArtifact(
                DiagnosticsBundleErrorCode.ArtifactTooLarge,
                "A diagnostics artifact exceeds the per-artifact size limit.",
                index);
        }

        var safety = DiagnosticsContentSafety.TrySanitizeArtifact(
            artifact.Content,
            out var sanitized);
        if (safety == DiagnosticsSafetyFailure.UnsafeContent)
        {
            return InvalidArtifact(
                DiagnosticsBundleErrorCode.UnsafeContent,
                "A diagnostics artifact did not pass the safety scan.",
                index);
        }

        if (safety != DiagnosticsSafetyFailure.None
            || !DiagnosticsContentSafety.TryEncodeUtf8(sanitized, out var content))
        {
            return InvalidArtifact(
                DiagnosticsBundleErrorCode.InvalidRequest,
                "A diagnostics artifact is not valid text.",
                index);
        }

        var entryPath = $"{ArtifactPathPrefix}{normalizedPath}";
        return DiagnosticsBundleResult<PreparedArtifact>.Success(new PreparedArtifact(
            entryPath,
            ArtifactKindName(artifact.Kind),
            content,
            Convert.ToHexStringLower(SHA256.HashData(content)),
            inputBytes.LongLength));
    }

    // ZIP paths use a deliberately smaller alphabet than any host filesystem. This avoids traversal,
    // platform aliases, case-only duplicates, and executable or sensitive-source artifacts.

    private static bool TryNormalizePath(
        string? value,
        DiagnosticsArtifactKind kind,
        out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string canonical;
        try
        {
            canonical = value.Normalize(NormalizationForm.FormC).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (canonical.StartsWith('/')
            || canonical.Contains(':')
            || DiagnosticsContentSafety.ContainsUnsafePathText(canonical))
        {
            return false;
        }

        var segments = new List<string>();
        foreach (var segment in canonical.Split('/'))
        {
            if (segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(segment, ".."
, StringComparison.Ordinal) || segment.Length > MaximumPathSegmentLength
                || !segment.All(IsPortablePathCharacter))
            {
                return false;
            }

            segments.Add(segment);
            if (segments.Count > MaximumPathSegmentCount)
            {
                return false;
            }
        }

        if (segments.Count == 0)
        {
            return false;
        }

        normalizedPath = string.Join('/', segments);
        if (!DiagnosticsContentSafety.TryEncodeUtf8(normalizedPath, out var encodedPath)
            || encodedPath.Length > DiagnosticsBundleLimits.MaximumRelativePathBytes
            || ContainsForbiddenPathFragment(normalizedPath)
            || !HasAllowedExtension(normalizedPath, kind))
        {
            normalizedPath = string.Empty;
            return false;
        }

        return true;
    }

    private static bool IsPortablePathCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';

    private static bool ContainsForbiddenPathFragment(string path)
    {
        foreach (var fragment in ForbiddenPathFragments)
        {
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAllowedExtension(string path, DiagnosticsArtifactKind kind)
    {
        var extension = Path.GetExtension(path);
        return kind switch
        {
            DiagnosticsArtifactKind.ApplicationLog =>
                extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase),
            DiagnosticsArtifactKind.CrashReport or DiagnosticsArtifactKind.ComponentStatus =>
                extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase),
            DiagnosticsArtifactKind.PerformanceSummary =>
                extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    // Serialization is fixed-order, uncompressed, and stamped with the ZIP epoch so equal canonical
    // requests produce equal bytes independently of input ordering or the current clock.

    private static byte[] BuildArchive(
        PreparedRequest request,
        CancellationToken cancellationToken)
    {
        var manifest = BuildManifest(request);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, ManifestPath, manifest);
            foreach (var artifact in request.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteEntry(archive, artifact.Path, artifact.Content);
            }
        }

        return buffer.ToArray();
    }

    private static byte[] BuildManifest(PreparedRequest request)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", ManifestSchemaVersion);
        writer.WriteString(
            "capturedAt",
            request.Metadata.CapturedAt.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture));
        writer.WriteString("applicationName", request.Metadata.ApplicationName);
        writer.WriteString("applicationIdentifier", request.Metadata.ApplicationIdentifier);
        writer.WriteString("executableName", request.Metadata.ExecutableName);
        writer.WriteString("applicationVersion", request.Metadata.ApplicationVersion);
        writer.WriteString("runtimeVersion", request.Metadata.RuntimeVersion);
        writer.WriteString("operatingSystem", request.Metadata.OperatingSystem);
        writer.WriteString("architecture", request.Metadata.Architecture);
        writer.WriteStartArray("artifacts");
        foreach (var artifact in request.Artifacts)
        {
            writer.WriteStartObject();
            writer.WriteString("path", artifact.Path);
            writer.WriteString("kind", artifact.Kind);
            writer.WriteString("mediaType", "text/plain; charset=utf-8");
            writer.WriteNumber("byteLength", artifact.Content.LongLength);
            writer.WriteString("sha256", artifact.Sha256);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = ZipEpoch;
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(content);
    }

    // Result construction stays centralized so no boundary exception or unsafe input is echoed back.

    private static string ArtifactKindName(DiagnosticsArtifactKind kind) => kind switch
    {
        DiagnosticsArtifactKind.ApplicationLog => "application-log",
        DiagnosticsArtifactKind.CrashReport => "crash-report",
        DiagnosticsArtifactKind.ComponentStatus => "component-status",
        DiagnosticsArtifactKind.PerformanceSummary => "performance-summary",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static bool CanWrite(Stream destination)
    {
        try
        {
            return destination.CanWrite;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsDestinationFailure(Exception exception) =>
        exception is IOException or NotSupportedException or ObjectDisposedException
        or UnauthorizedAccessException or InvalidOperationException;

    private static DiagnosticsBundleResult<PreparedRequest> InvalidRequest(string message) =>
        DiagnosticsBundleResult<PreparedRequest>.Failure(
            new DiagnosticsBundleError(DiagnosticsBundleErrorCode.InvalidRequest, message));

    private static DiagnosticsBundleResult<PreparedArtifact> InvalidArtifact(
        DiagnosticsBundleErrorCode code,
        string message,
        int index) =>
        DiagnosticsBundleResult<PreparedArtifact>.Failure(
            new DiagnosticsBundleError(code, message, index));

    private static DiagnosticsBundleResult<DiagnosticsBundleReceipt> Failure(
        DiagnosticsBundleErrorCode code,
        string message) =>
        DiagnosticsBundleResult<DiagnosticsBundleReceipt>.Failure(
            new DiagnosticsBundleError(code, message));

    private sealed record PreparedMetadata(
        string ApplicationName,
        string ApplicationIdentifier,
        string ExecutableName,
        string ApplicationVersion,
        string RuntimeVersion,
        string OperatingSystem,
        string Architecture,
        DateTimeOffset CapturedAt);

    private sealed record PreparedArtifact(
        string Path,
        string Kind,
        byte[] Content,
        string Sha256,
        long InputByteLength);

    private sealed record PreparedRequest(
        PreparedMetadata Metadata,
        IReadOnlyList<PreparedArtifact> Artifacts,
        long TotalArtifactBytes);
}

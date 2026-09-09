using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Application;

namespace Asura.Infrastructure.Tests;

public sealed class DeterministicDiagnosticsBundleExporterTests
{
    private static readonly DateTimeOffset CapturedAt =
        new(2026, 7, 22, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExportProducesCanonicalZipAndClosedManifest()
    {
        var request = Request(
            new DiagnosticsBundleArtifact(
                "status/runtime.json",
                DiagnosticsArtifactKind.ComponentStatus,
                "{\"state\":\"ready\"}"),
            new DiagnosticsBundleArtifact(
                "logs/application.log",
                DiagnosticsArtifactKind.ApplicationLog,
                "level=info state=ready"));
        await using var destination = new MemoryStream();

        var result = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            request,
            destination,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.ArtifactCount);
        Assert.Equal(destination.Length, result.Value.ArchiveBytes);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(destination.ToArray())),
            result.Value.Sha256);

        using var archive = OpenArchive(destination);
        Assert.Equal(
            [
                "manifest.json",
                "artifacts/logs/application.log",
                "artifacts/status/runtime.json",
            ],
            archive.Entries.Select(entry => entry.FullName), StringComparer.Ordinal);
        Assert.All(
            archive.Entries,
            entry => Assert.Equal(
                new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
                entry.LastWriteTime.DateTime));

        using var manifest = JsonDocument.Parse(ReadEntry(archive, "manifest.json"));
        var root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-07-22T12:30:00.0000000Z", root.GetProperty("capturedAt").GetString());
        Assert.Equal("Asura", root.GetProperty("applicationName").GetString());
        Assert.Equal("sh.asura", root.GetProperty("applicationIdentifier").GetString());
        Assert.Equal("Asura", root.GetProperty("executableName").GetString());
        Assert.Equal("0.1.0", root.GetProperty("applicationVersion").GetString());
        Assert.Equal(".NET 10.0.0", root.GetProperty("runtimeVersion").GetString());
        Assert.Equal("macOS 15.5", root.GetProperty("operatingSystem").GetString());
        Assert.Equal("arm64", root.GetProperty("architecture").GetString());
        Assert.Equal(2, root.GetProperty("artifacts").GetArrayLength());
        Assert.Equal(10, root.GetPropertyCount());
    }

    [Fact]
    public async Task EquivalentCanonicalInputsProduceIdenticalBytes()
    {
        var first = new DiagnosticsBundleRequest(
            Metadata(CapturedAt.ToOffset(TimeSpan.FromHours(3))),
            [
                new DiagnosticsBundleArtifact(
                    "status\\./runtime.json",
                    DiagnosticsArtifactKind.ComponentStatus,
                    "{\"state\":\"ready\"}\r\n"),
                new DiagnosticsBundleArtifact(
                    "logs//application.log",
                    DiagnosticsArtifactKind.ApplicationLog,
                    "line one\r\nline two"),
            ]);
        var second = new DiagnosticsBundleRequest(
            Metadata(CapturedAt),
            [
                new DiagnosticsBundleArtifact(
                    "logs/application.log",
                    DiagnosticsArtifactKind.ApplicationLog,
                    "line one\nline two"),
                new DiagnosticsBundleArtifact(
                    "status/runtime.json",
                    DiagnosticsArtifactKind.ComponentStatus,
                    "{\"state\":\"ready\"}\n"),
            ]);

        var firstBytes = await ExportBytesAsync(first);
        var secondBytes = await ExportBytesAsync(second);

        Assert.Equal(firstBytes, secondBytes);
    }

    [Fact]
    public async Task EmptyArtifactRequestDoesNotDiscoverOrAddRuntimeState()
    {
        await using var destination = new MemoryStream();

        var result = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            Request(),
            destination,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(0, result.Value!.ArtifactCount);
        using var archive = OpenArchive(destination);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("manifest.json", entry.FullName);
        var manifest = ReadEntry(archive, "manifest.json");
        Assert.DoesNotContain("terminal", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecognizedSecretAssignmentsAreRedactedBeforeExport()
    {
        const string password = "password-canary-77";
        const string authorization = "authorization-canary-88";
        var request = Request(new DiagnosticsBundleArtifact(
            "logs/application.log",
            DiagnosticsArtifactKind.ApplicationLog,
            $$"""
            {"state":"failed","password":"{{password}}","code":7}
            authorization: Bearer {{authorization}}
            """));
        await using var destination = new MemoryStream();

        var result = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            request,
            destination,
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(destination.ToArray()), StringComparison.Ordinal);
        Assert.DoesNotContain(authorization, Encoding.UTF8.GetString(destination.ToArray()), StringComparison.Ordinal);
        using var archive = OpenArchive(destination);
        var content = ReadEntry(archive, "artifacts/logs/application.log");
        Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
        Assert.DoesNotContain(password, content, StringComparison.Ordinal);
        Assert.DoesNotContain(authorization, content, StringComparison.Ordinal);
        Assert.Contains("\"code\":7", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"command\":\"ssh production\"}")]
    [InlineData("username=operator")]
    [InlineData("credential=opaque-value")]
    [InlineData("$ ssh production")]
    [InlineData("PATH=/usr/local/bin")]
    [InlineData("https://operator:password@example.test/status")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    [InlineData("AKIA0123456789ABCDEF")]
    [InlineData("ghp_012345678901234567890123456789")]
    [InlineData("sk-01234567890123456789")]
    [InlineData("xoxb-0123456789012345")]
    [InlineData("AIza012345678901234567890123456789")]
    [InlineData("eyJ0123456789.eyJ0123456789.abcdefghi012345")]
    public async Task UnsafeContentIsRejectedBeforeDestinationWrite(string unsafeContent)
    {
        var request = Request(new DiagnosticsBundleArtifact(
            "logs/application.log",
            DiagnosticsArtifactKind.ApplicationLog,
            unsafeContent));
        await using var destination = new MemoryStream([1, 2, 3]);
        destination.Position = destination.Length;

        var result = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            request,
            destination,
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(DiagnosticsBundleErrorCode.UnsafeContent, result.Error!.Code);
        Assert.Equal(new byte[] { 1, 2, 3 }, destination.ToArray());
        Assert.DoesNotContain(unsafeContent, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousStructuredSecretIsRejectedRatherThanPartiallyRedacted()
    {
        var request = Request(new DiagnosticsBundleArtifact(
            "status/runtime.json",
            DiagnosticsArtifactKind.ComponentStatus,
            "{\"password\":{\"encoded\":\"canary\"}}"));

        var result = await ExportAsync(request);

        Assert.Equal(DiagnosticsBundleErrorCode.UnsafeContent, result.Error!.Code);
        Assert.Equal(0, result.Error.ArtifactIndex);
    }

    [Theory]
    [InlineData("../application.log")]
    [InlineData("logs/../../application.log")]
    [InlineData("/logs/application.log")]
    [InlineData("C:\\logs\\application.log")]
    [InlineData("terminal/output.log")]
    [InlineData("logs/application.exe")]
    [InlineData("logs/app lic ation.log")]
    public async Task UnsafeOrNonPortablePathsAreRejected(string path)
    {
        var request = Request(new DiagnosticsBundleArtifact(
            path,
            DiagnosticsArtifactKind.ApplicationLog,
            "level=info"));

        var result = await ExportAsync(request);

        Assert.Equal(DiagnosticsBundleErrorCode.InvalidPath, result.Error!.Code);
        Assert.Equal(0, result.Error.ArtifactIndex);
        Assert.DoesNotContain(path, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicallyEquivalentPathsCannotCreateAmbiguousZipEntries()
    {
        var request = Request(
            new DiagnosticsBundleArtifact(
                "logs\\application.log",
                DiagnosticsArtifactKind.ApplicationLog,
                "first"),
            new DiagnosticsBundleArtifact(
                "logs/./APPLICATION.log",
                DiagnosticsArtifactKind.ApplicationLog,
                "second"));

        var result = await ExportAsync(request);

        Assert.Equal(DiagnosticsBundleErrorCode.DuplicatePath, result.Error!.Code);
        Assert.Equal(1, result.Error.ArtifactIndex);
    }

    [Fact]
    public async Task ArtifactCountLimitIsEnforcedBeforeArchiveCreation()
    {
        var artifacts = Enumerable.Range(0, DiagnosticsBundleLimits.MaximumArtifactCount + 1)
            .Select(index => new DiagnosticsBundleArtifact(
                $"logs/application-{index}.log",
                DiagnosticsArtifactKind.ApplicationLog,
                "ready"))
            .ToArray();

        var result = await ExportAsync(Request(artifacts));

        Assert.Equal(DiagnosticsBundleErrorCode.TooManyArtifacts, result.Error!.Code);
    }

    [Fact]
    public async Task PerArtifactSizeLimitIsMeasuredInUtf8Bytes()
    {
        var content = new string('\u00E9', (DiagnosticsBundleLimits.MaximumArtifactBytes / 2) + 1);
        var request = Request(new DiagnosticsBundleArtifact(
            "logs/application.log",
            DiagnosticsArtifactKind.ApplicationLog,
            content));

        var result = await ExportAsync(request);

        Assert.Equal(DiagnosticsBundleErrorCode.ArtifactTooLarge, result.Error!.Code);
    }

    [Fact]
    public async Task TotalArtifactSizeLimitIsEnforced()
    {
        var content = new string('a', 950_000);
        var artifacts = Enumerable.Range(0, 9)
            .Select(index => new DiagnosticsBundleArtifact(
                $"logs/application-{index}.log",
                DiagnosticsArtifactKind.ApplicationLog,
                content))
            .ToArray();

        var result = await ExportAsync(Request(artifacts));

        Assert.Equal(DiagnosticsBundleErrorCode.BundleTooLarge, result.Error!.Code);
        Assert.Equal(8, result.Error.ArtifactIndex);
    }

    [Fact]
    public async Task MetadataIsBoundedAndSafetyScanned()
    {
        var oversized = Metadata(CapturedAt) with
        {
            OperatingSystem = new string('x', DiagnosticsBundleLimits.MaximumMetadataValueBytes + 1),
        };
        var unsafeMetadata = Metadata(CapturedAt) with
        {
            RuntimeVersion = "apiKey=metadata-canary",
        };

        var oversizedResult = await ExportAsync(new DiagnosticsBundleRequest(oversized, []));
        var unsafeResult = await ExportAsync(new DiagnosticsBundleRequest(unsafeMetadata, []));

        Assert.Equal(DiagnosticsBundleErrorCode.InvalidRequest, oversizedResult.Error!.Code);
        Assert.Equal(DiagnosticsBundleErrorCode.UnsafeContent, unsafeResult.Error!.Code);
        Assert.DoesNotContain("metadata-canary", unsafeResult.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidArtifactKindIsRejected()
    {
        var request = Request(new DiagnosticsBundleArtifact(
            "logs/application.log",
            (DiagnosticsArtifactKind)999,
            "ready"));

        var result = await ExportAsync(request);

        Assert.Equal(DiagnosticsBundleErrorCode.InvalidRequest, result.Error!.Code);
    }

    [Fact]
    public async Task CancellationAndNonWritableDestinationsReturnTypedFailures()
    {
        var request = Request();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var cancelledDestination = new MemoryStream();
        await using var readOnlyDestination = new MemoryStream(new byte[1], writable: false);

        var cancelled = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            request,
            cancelledDestination,
            cancellation.Token);
        var unavailable = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            request,
            readOnlyDestination,
            CancellationToken.None);

        Assert.Equal(DiagnosticsBundleErrorCode.Cancelled, cancelled.Error!.Code);
        Assert.Empty(cancelledDestination.ToArray());
        Assert.Equal(DiagnosticsBundleErrorCode.DestinationUnavailable, unavailable.Error!.Code);
    }

    [Fact]
    public async Task DestinationIoFailureIsSanitized()
    {
        await using var destination = new FailingWriteStream();

        var result = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            Request(),
            destination,
            CancellationToken.None);

        Assert.Equal(DiagnosticsBundleErrorCode.DestinationUnavailable, result.Error!.Code);
        Assert.DoesNotContain("filesystem-canary", result.Error.Message, StringComparison.Ordinal);
    }

    private static async Task<byte[]> ExportBytesAsync(DiagnosticsBundleRequest request)
    {
        await using var destination = new MemoryStream();
        var result = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            request,
            destination,
            CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return destination.ToArray();
    }

    private static async Task<DiagnosticsBundleResult<DiagnosticsBundleReceipt>> ExportAsync(
        DiagnosticsBundleRequest request)
    {
        await using var destination = new MemoryStream();
        return await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            request,
            destination,
            CancellationToken.None);
    }

    private static DiagnosticsBundleRequest Request(
        params DiagnosticsBundleArtifact[] artifacts) =>
        new(Metadata(CapturedAt), artifacts);

    private static DiagnosticsBundleMetadata Metadata(DateTimeOffset capturedAt) => new(
        ProductIdentity.DisplayName,
        ProductIdentity.BundleIdentifier,
        ProductIdentity.ExecutableName,
        "0.1.0",
        ".NET 10.0.0",
        "macOS 15.5",
        "arm64",
        capturedAt);

    private static ZipArchive OpenArchive(MemoryStream stream)
    {
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    }

    private static string ReadEntry(ZipArchive archive, string path)
    {
        var entry = Assert.Single(archive.Entries, candidate => string.Equals(candidate.FullName, path, StringComparison.Ordinal));
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, leaveOpen: false);
        return reader.ReadToEnd();
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("filesystem-canary"));
    }
}

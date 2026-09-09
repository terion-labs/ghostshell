using System.IO.Compression;
using Asura.App;
using Asura.Application;
using Asura.Desktop;
using Asura.Infrastructure;

namespace Asura.Architecture.Tests;

public sealed class DiagnosticsCompositionTests
{
    [Fact]
    public async Task DesktopSafeSourceProducesAClosedRequestAcceptedByTheExporter()
    {
        var availability = new SecretVaultAvailability(
            SecretVaultAvailabilityState.Available,
            SecretVaultPersistenceKind.OsProtectedPersistent,
            SecretVaultCapabilities.All,
            "platform-vault",
            "ready",
            "Ready");
        var diagnostic = new SecretVaultFactoryDiagnostic(
            SecretVaultPlatform.MacOS,
            "platform-vault",
            "ready",
            "Ready",
            availability);
        var source = new SafeDiagnosticsBundleRequestSource(
            diagnostic,
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 30, 0, TimeSpan.Zero)));

        Assert.Equal([DiagnosticsRedactionLevel.Safe], source.SupportedRedactionLevels);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await source.CreateRequestAsync(DiagnosticsRedactionLevel.Full, CancellationToken.None));

        var request = await source.CreateRequestAsync(
            DiagnosticsRedactionLevel.Safe,
            CancellationToken.None);
        Assert.Equal(ProductIdentity.DisplayName, request.Metadata.ApplicationName);
        Assert.Equal(ProductIdentity.BundleIdentifier, request.Metadata.ApplicationIdentifier);
        Assert.Equal(ProductIdentity.ExecutableName, request.Metadata.ExecutableName);
        await using var output = new MemoryStream();
        var result = await new DeterministicDiagnosticsBundleExporter().ExportAsync(
            request,
            output,
            CancellationToken.None);

        Assert.True(
            result.IsSuccess,
            result.Error is null
                ? null
                : $"{result.Error.Code}: {result.Error.Message} Artifact index: {result.Error.ArtifactIndex}");
        Assert.Equal(2, result.Value!.ArtifactCount);
        output.Position = 0;
        using var archive = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        Assert.Equal(
            [
                "manifest.json",
                "artifacts/performance/performance-summary.txt",
                "artifacts/status/component-status.txt",
            ],
            archive.Entries.Select(entry => entry.FullName), StringComparer.Ordinal);
        var exportedText = string.Join(
            '\n',
            archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain("terminal", exportedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", exportedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", exportedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", exportedText, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

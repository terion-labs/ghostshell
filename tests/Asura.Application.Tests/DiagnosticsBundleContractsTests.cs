using Asura.Application;

namespace Asura.Application.Tests;

public sealed class DiagnosticsBundleContractsTests
{
    [Fact]
    public void RequestSnapshotsTheExplicitArtifactCollection()
    {
        var artifacts = new List<DiagnosticsBundleArtifact>
        {
            new("logs/application.log", DiagnosticsArtifactKind.ApplicationLog, "ready"),
        };
        var request = new DiagnosticsBundleRequest(Metadata(), artifacts);

        artifacts.Clear();

        Assert.Single(request.Artifacts);
        Assert.IsAssignableFrom<IReadOnlyList<DiagnosticsBundleArtifact>>(request.Artifacts);
    }

    [Fact]
    public void ArtifactKindsExcludeSensitiveRuntimeSources()
    {
        var names = Enum.GetNames<DiagnosticsArtifactKind>();

        Assert.Equal(
            [
                nameof(DiagnosticsArtifactKind.ApplicationLog),
                nameof(DiagnosticsArtifactKind.CrashReport),
                nameof(DiagnosticsArtifactKind.ComponentStatus),
                nameof(DiagnosticsArtifactKind.PerformanceSummary),
            ],
            names);
        Assert.DoesNotContain(names, name => ContainsSensitiveSourceName(name));
    }

    [Fact]
    public void MetadataHasNoMachineUserCommandOrEnvironmentValueField()
    {
        var propertyNames = typeof(DiagnosticsBundleMetadata)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name => ContainsSensitiveSourceName(name));
        Assert.DoesNotContain("MachineName", propertyNames, StringComparer.Ordinal);
        Assert.DoesNotContain("UserName", propertyNames, StringComparer.Ordinal);
        Assert.DoesNotContain("ProcessArguments", propertyNames, StringComparer.Ordinal);
    }

    [Fact]
    public void FixedLimitsKeepEveryArtifactBelowTheWholeBundleCeiling()
    {
        Assert.InRange(DiagnosticsBundleLimits.MaximumArtifactCount, 1, 64);
        Assert.InRange(DiagnosticsBundleLimits.MaximumArtifactBytes, 1, 2 * 1024 * 1024);
        Assert.True(
            DiagnosticsBundleLimits.MaximumArtifactBytes
            < DiagnosticsBundleLimits.MaximumTotalArtifactBytes);
        Assert.True(
            DiagnosticsBundleLimits.MaximumTotalArtifactBytes
            < DiagnosticsBundleLimits.MaximumArchiveBytes);
        Assert.InRange(DiagnosticsBundleLimits.MaximumRelativePathBytes, 1, 256);
        Assert.InRange(DiagnosticsBundleLimits.MaximumMetadataValueBytes, 1, 512);
    }

    [Fact]
    public void ResultRepresentsExactlyOneOutcome()
    {
        var receipt = new DiagnosticsBundleReceipt(0, 0, 128, new string('a', 64));
        var success = DiagnosticsBundleResult<DiagnosticsBundleReceipt>.Success(receipt);
        var failure = DiagnosticsBundleResult<DiagnosticsBundleReceipt>.Failure(
            new DiagnosticsBundleError(
                DiagnosticsBundleErrorCode.UnsafeContent,
                "Unsafe diagnostics content."));

        Assert.True(success.IsSuccess);
        Assert.Same(receipt, success.Value);
        Assert.Null(success.Error);
        Assert.False(failure.IsSuccess);
        Assert.Null(failure.Value);
        Assert.NotNull(failure.Error);
    }

    private static bool ContainsSensitiveSourceName(string value) =>
        value.Contains("Terminal", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Command", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Credential", StringComparison.OrdinalIgnoreCase)
        || value.Contains("Secret", StringComparison.OrdinalIgnoreCase)
        || value.Contains("EnvironmentValue", StringComparison.OrdinalIgnoreCase);

    private static DiagnosticsBundleMetadata Metadata() => new(
        ProductIdentity.DisplayName,
        ProductIdentity.BundleIdentifier,
        ProductIdentity.ExecutableName,
        "0.1.0",
        ".NET 10.0.0",
        "macOS 15.5",
        "arm64",
        new DateTimeOffset(2026, 7, 22, 12, 30, 0, TimeSpan.Zero));
}

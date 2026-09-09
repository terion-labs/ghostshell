using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Asura.App;
using Asura.Application;
using Asura.Infrastructure;

namespace Asura.Desktop;

/// <summary>
/// Produces only closed, non-identifying status and performance summaries. It has no access to
/// terminal contents, process arguments, environment values, durable definitions, or secrets.
/// </summary>
internal sealed class SafeDiagnosticsBundleRequestSource : IDiagnosticsBundleRequestSource
{
    private static readonly IReadOnlyList<DiagnosticsRedactionLevel> SupportedLevels =
        Array.AsReadOnly([DiagnosticsRedactionLevel.Safe]);
    private readonly SecretVaultFactoryDiagnostic _vaultDiagnostic;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;

    public SafeDiagnosticsBundleRequestSource(
        SecretVaultFactoryDiagnostic vaultDiagnostic,
        TimeProvider timeProvider)
    {
        _vaultDiagnostic = vaultDiagnostic
            ?? throw new ArgumentNullException(nameof(vaultDiagnostic));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startedAt = _timeProvider.GetUtcNow();
    }

    public IReadOnlyList<DiagnosticsRedactionLevel> SupportedRedactionLevels => SupportedLevels;

    public ValueTask<DiagnosticsBundleRequest> CreateRequestAsync(
        DiagnosticsRedactionLevel redactionLevel,
        CancellationToken cancellationToken)
    {
        if (redactionLevel != DiagnosticsRedactionLevel.Safe)
        {
            throw new ArgumentOutOfRangeException(
                nameof(redactionLevel),
                redactionLevel,
                "This installation supports safe-summary diagnostics only.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = _timeProvider.GetUtcNow();
        var assemblyVersion = typeof(DesktopComposition).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var componentStatus = new StringBuilder()
            .AppendLine("catalog_state=ready")
            .AppendLine("host_mode=in-process")
            .Append("vault_state=")
            .AppendLine(_vaultDiagnostic.Availability.State.ToString())
            .Append("vault_persistence=")
            .AppendLine(_vaultDiagnostic.Availability.Persistence.ToString())
            .Append("vault_adapter=")
            .AppendLine(_vaultDiagnostic.Adapter)
            .ToString();
        var gc = GC.GetGCMemoryInfo();
        var performance = new StringBuilder()
            .Append("managed_memory_bytes=")
            .AppendLine(GC.GetTotalMemory(forceFullCollection: false).ToString(CultureInfo.InvariantCulture))
            .Append("gc_heap_bytes=")
            .AppendLine(gc.HeapSizeBytes.ToString(CultureInfo.InvariantCulture))
            .Append("processor_count=")
            .AppendLine(System.Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture))
            .Append("process_uptime_seconds=")
            .AppendLine(Math.Max(0, (capturedAt - _startedAt).TotalSeconds)
                .ToString("0", CultureInfo.InvariantCulture))
            .ToString();

        return ValueTask.FromResult(new DiagnosticsBundleRequest(
            new DiagnosticsBundleMetadata(
                ProductIdentity.DisplayName,
                ProductIdentity.BundleIdentifier,
                ProductIdentity.ExecutableName,
                assemblyVersion,
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                capturedAt),
            [
                new DiagnosticsBundleArtifact(
                    "status/component-status.txt",
                    DiagnosticsArtifactKind.ComponentStatus,
                    componentStatus),
                new DiagnosticsBundleArtifact(
                    "performance/performance-summary.txt",
                    DiagnosticsArtifactKind.PerformanceSummary,
                    performance),
            ]));
    }
}

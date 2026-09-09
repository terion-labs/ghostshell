namespace Asura.Application;

/// <summary>
/// Writes a bounded support bundle from caller-supplied safe diagnostics only. Implementations must
/// not discover terminal state, commands, credentials, environment values, or secret material.
/// </summary>
public interface IDiagnosticsBundleExporter
{
    ValueTask<DiagnosticsBundleResult<DiagnosticsBundleReceipt>> ExportAsync(
        DiagnosticsBundleRequest request,
        Stream destination,
        CancellationToken cancellationToken);
}

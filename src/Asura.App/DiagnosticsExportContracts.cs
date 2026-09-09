using Asura.Application;

namespace Asura.App;

public enum DiagnosticsRedactionLevel
{
    Safe = 1,
    Full = 2,
}

/// <summary>
/// Supplies explicit, bounded diagnostics for export. Safe mode should contain only status and
/// summary artifacts. Full mode may add eligible logs and crash reports, but never relaxes the
/// exporter's mandatory secret scanning and redaction boundary.
/// </summary>
public interface IDiagnosticsBundleRequestSource
{
    IReadOnlyList<DiagnosticsRedactionLevel> SupportedRedactionLevels { get; }

    ValueTask<DiagnosticsBundleRequest> CreateRequestAsync(
        DiagnosticsRedactionLevel redactionLevel,
        CancellationToken cancellationToken);
}

/// <summary>
/// Identifies an exported artifact without requiring the view model to understand platform paths
/// or storage handles. The picker and presenter agree on the opaque locator.
/// </summary>
public sealed record DiagnosticsGeneratedArtifact(
    string DisplayName,
    string Locator);

/// <summary>
/// Owns a selected export stream until it is either completed or disposed. Implementations may use
/// a temporary file and publish it atomically from <see cref="CompleteAsync"/>.
/// </summary>
public interface IDiagnosticsBundleDestination : IAsyncDisposable
{
    DiagnosticsGeneratedArtifact Artifact { get; }

    Stream Content { get; }

    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

public interface IDiagnosticsBundleDestinationPicker
{
    /// <summary>
    /// Opens the host save dialog. Avalonia's native picker contract has no programmatic
    /// cancellation mechanism, so callers must treat this as a distinct non-cancellable phase.
    /// </summary>
    ValueTask<IDiagnosticsBundleDestination?> PickAsync(string suggestedFileName);
}

[Flags]
public enum DiagnosticsArtifactPresentationCapabilities
{
    None = 0,
    Open = 1,
    Reveal = 2,
}

public enum DiagnosticsArtifactPresentationAction
{
    Open,
    Reveal,
}

public enum DiagnosticsArtifactPresentationResult
{
    Presented,
    Unsupported,
    Failed,
}

/// <summary>
/// Opens an artifact or reveals it in the host file manager through platform integration. View
/// models must not construct shell commands from the artifact locator.
/// </summary>
public interface IDiagnosticsArtifactPresenter
{
    DiagnosticsArtifactPresentationCapabilities Capabilities { get; }

    ValueTask<DiagnosticsArtifactPresentationResult> PresentAsync(
        DiagnosticsGeneratedArtifact artifact,
        DiagnosticsArtifactPresentationAction action,
        CancellationToken cancellationToken);
}

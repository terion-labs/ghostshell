namespace Asura.Application;

/// <summary>
/// A sanitized export failure. Messages never repeat artifact paths or content.
/// </summary>
public sealed record DiagnosticsBundleError(
    DiagnosticsBundleErrorCode Code,
    string Message,
    int? ArtifactIndex = null);

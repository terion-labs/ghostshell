namespace Asura.Application;

/// <summary>
/// An explicitly supplied UTF-8 text artifact. The exporter treats the content as untrusted and
/// scans it before writing anything to the destination.
/// </summary>
public sealed record DiagnosticsBundleArtifact(
    string RelativePath,
    DiagnosticsArtifactKind Kind,
    string Content);

namespace Asura.Application;

public sealed record DiagnosticsBundleReceipt(
    int ArtifactCount,
    long TotalArtifactBytes,
    long ArchiveBytes,
    string Sha256);

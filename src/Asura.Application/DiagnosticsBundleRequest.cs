namespace Asura.Application;

public sealed record DiagnosticsBundleRequest
{
    public DiagnosticsBundleRequest(
        DiagnosticsBundleMetadata metadata,
        IReadOnlyList<DiagnosticsBundleArtifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(artifacts);

        Metadata = metadata;
        Artifacts = Array.AsReadOnly(artifacts.ToArray());
    }

    public DiagnosticsBundleMetadata Metadata { get; }

    public IReadOnlyList<DiagnosticsBundleArtifact> Artifacts { get; }
}

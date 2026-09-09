using System.Text.Json.Serialization;

namespace Asura.SecurityCampaign;

internal sealed record CampaignDefinition(
    int SchemaVersion,
    string Format,
    string ReleaseScope,
    IReadOnlyList<string> DeferredPlatforms,
    IReadOnlyList<string> ActionExceptions,
    IReadOnlyList<CampaignCaseDefinition> Cases);

internal sealed record CampaignCaseDefinition(
    string Id,
    string Kind,
    string TestProject,
    string TestSource,
    string TestNameContains);

internal sealed record CampaignReceipt(
    int SchemaVersion,
    string EvidenceKind,
    string EvidenceClass,
    IReadOnlyList<PlatformEvidence> ReleaseScope,
    SourceEvidence Source,
    DefinitionEvidence Definition,
    IReadOnlyList<CaseEvidence> Cases,
    SecrecyEvidence Secrecy,
    CandidateEvidence? Candidate,
    DependencyEvidence? Dependencies,
    IReadOnlyList<FileEvidence> Components,
    SigningEvidence? Signing,
    IReadOnlyList<string> Limitations,
    string Overall);

internal sealed record PlatformEvidence(
    string Platform,
    string Rid,
    string Status,
    string ReasonCode);

internal sealed record SourceEvidence(
    string Repository,
    string Commit,
    string Tree,
    string SourceArchiveSha256,
    string? SourceSealSha256,
    string? SourceManifestSha256,
    string? Tag,
    string? WorkflowSha256,
    string? RunId,
    string? RunAttempt);

internal sealed record DefinitionEvidence(
    string RegistrySha256,
    string ReceiptSchemaSha256,
    string ToolCatalogSha256);

internal sealed record CaseEvidence(
    string Id,
    string TestName,
    string TrxSha256,
    string Result);

internal sealed record SecrecyEvidence(
    string CanarySetSha256,
    int CanaryCount,
    int SecrecyCaseCount,
    int ZeroMatchCaseCount);

internal sealed record CandidateEvidence(
    string ArchiveName,
    long ArchiveBytes,
    string ArchiveSha256,
    string PackageManifestSha256,
    int PackageFileCount,
    string ExecutableName,
    long ExecutableBytes,
    string ExecutableSha256,
    string SourceSealSha256,
    string SourceManifestSha256,
    string BuildIdentitySha256,
    string BundleIdentifier,
    string ProductVersion);

internal sealed record ReleaseSourceSealDocument(
    int SchemaVersion,
    string Format,
    string Repository,
    string Tag,
    string Commit,
    string Tree,
    string SourceArchiveSha256,
    string SealSchemaSha256,
    string ManifestSha256,
    IReadOnlyList<ReleaseSourceManifestEntry> Files,
    IReadOnlyList<string> GeneratedRoots);

internal sealed record ReleaseSourceManifestEntry(
    string RelativePath,
    string Mode,
    long Bytes,
    string Sha256);

internal sealed record ReleaseBuildIdentityDocument(
    int SchemaVersion,
    string Format,
    string SourceSealSha256,
    string SealedManifestSha256,
    string ObservedManifestSha256,
    string Status);

internal sealed record DependencyEvidence(
    string Format,
    string SourceCommit,
    string Status,
    IReadOnlyList<FileEvidence> Inputs,
    int UntriagedAdvisories,
    int ReleaseBlockingAdvisories);

internal sealed record FileEvidence(string Kind, string RelativePath, long Bytes, string Sha256);

internal sealed record SigningEvidence(
    string Format,
    string NotarizationId,
    string NotarizationStatus,
    string TeamIdentifier,
    string CertificateSha256,
    bool CodeSignatureValid,
    bool StapleValid,
    bool GatekeeperAccepted);

internal sealed record DependencyEvidenceDocument(
    int SchemaVersion,
    string Format,
    string SourceCommit,
    string Status,
    IReadOnlyList<FileEvidence> Inputs,
    int UntriagedAdvisories,
    int ReleaseBlockingAdvisories);

internal sealed record SigningEvidenceDocument(
    int SchemaVersion,
    string Format,
    string NotarizationId,
    string NotarizationStatus,
    string TeamIdentifier,
    string CertificateSha256,
    bool CodeSignatureValid,
    bool StapleValid,
    bool GatekeeperAccepted);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CampaignDefinition))]
[JsonSerializable(typeof(CampaignReceipt))]
[JsonSerializable(typeof(DependencyEvidenceDocument))]
[JsonSerializable(typeof(SigningEvidenceDocument))]
[JsonSerializable(typeof(ReleaseSourceSealDocument))]
[JsonSerializable(typeof(ReleaseBuildIdentityDocument))]
internal sealed partial class CampaignJsonContext : JsonSerializerContext;

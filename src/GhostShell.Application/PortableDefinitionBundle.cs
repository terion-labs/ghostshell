using System.Text.Json.Serialization;
using GhostShell.Core;

namespace GhostShell.Application;

public sealed record PortableDefinitionDocument(
    DefinitionKind Kind,
    string Id,
    int SchemaVersion,
    string Name,
    string PayloadJson);

public sealed record PortableDefinitionBundle(
    int FormatVersion,
    DateTimeOffset ExportedAt,
    IReadOnlyList<PortableDefinitionDocument> Definitions)
{
    public const int CurrentFormatVersion = 1;

    [JsonIgnore]
    public int ReconnectRequiredDatabasePanelCount { get; init; }
}

public enum DefinitionImportMode
{
    FailOnConflict,
    ReplaceExisting,
}

public enum DefinitionImportIssueCode
{
    InvalidBundle,
    InvalidPayload,
    UnsafePayload,
    DuplicateIdentity,
    ExistingIdentity,
    UnsupportedKind,
    UnsupportedSchema,
    MissingDependency,
    ImportedMcpProfileDisabled,
    ImportedAiProviderProfileDisabled,
    ImportedBrowserProfileDisabled,
    ImportedNetworkCredentialsDetached,
    ImportedNetworkPolicyDisabled,
    ImportedDatabaseRecoveryDetached,
}

public sealed record DefinitionImportIssue(
    DefinitionImportIssueCode Code,
    DefinitionKey? Definition,
    string Message,
    bool IsBlocking);

public sealed record DefinitionImportPreflight(
    PortableDefinitionBundle Bundle,
    DefinitionImportMode Mode,
    IReadOnlyList<DefinitionImportIssue> Issues)
{
    public PortableDefinitionBundle Bundle { get; } = Bundle with
    {
        Definitions = Array.AsReadOnly(Bundle.Definitions.ToArray()),
    };

    public IReadOnlyList<DefinitionExecutionReviewItem> ExecutionReview { get; init; } = [];

    public string? CatalogFingerprint { get; init; }

    public bool CanCommit => Issues.All(issue => !issue.IsBlocking);

    /// <summary>Called only after an explicit user review, never from portable payload data.</summary>
    public DefinitionImportExecutionApproval AcknowledgeExecutionReview() => new(this);
}

public sealed record DefinitionExecutionReviewItem(string Heading, string Details);

/// <summary>In-process approval for one exact frozen preflight; not serializable import state.</summary>
public sealed class DefinitionImportExecutionApproval
{
    private readonly DefinitionImportPreflight _preflight;

    internal DefinitionImportExecutionApproval(DefinitionImportPreflight preflight) => _preflight = preflight;

    public bool AppliesTo(DefinitionImportPreflight preflight) => ReferenceEquals(_preflight, preflight);
}

public sealed record DefinitionImportResult(int Inserted, int Replaced);

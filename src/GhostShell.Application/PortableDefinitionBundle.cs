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
    public bool CanCommit => Issues.All(issue => !issue.IsBlocking);
}

public sealed record DefinitionImportResult(int Inserted, int Replaced);

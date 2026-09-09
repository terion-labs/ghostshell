namespace Asura.Application;

public enum DefinitionStoreErrorCode
{
    NotFound,
    RevisionConflict,
    InvalidDefinition,
    UnsupportedKind,
    UnsupportedSchema,
    DependencyConflict,
    UnsafePayload,
    StorageUnavailable,
    StorageFailure,
    Cancelled,
}

public sealed record DefinitionStoreError(
    DefinitionStoreErrorCode Code,
    string Message,
    long? CurrentRevision = null);

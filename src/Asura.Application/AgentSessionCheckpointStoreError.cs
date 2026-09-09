namespace Asura.Application;

public enum AgentSessionCheckpointStoreErrorCode
{
    NotFound,
    RevisionConflict,
    InvalidCheckpoint,
    CorruptData,
    StorageUnavailable,
    StorageFailure,
    Cancelled,
}

public sealed record AgentSessionCheckpointStoreError(
    AgentSessionCheckpointStoreErrorCode Code,
    string Message,
    long? CurrentRevision = null);

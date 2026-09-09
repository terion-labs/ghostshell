namespace Asura.Application;

public sealed record RuntimeRecoverySnapshot(
    string RunId,
    string Key,
    int SchemaVersion,
    string PayloadJson,
    DateTimeOffset UpdatedAt);

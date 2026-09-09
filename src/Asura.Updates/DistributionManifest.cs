namespace Asura.Updates;

internal sealed record DistributionManifest(
    int SchemaVersion,
    string Source,
    string UpdateStrategy,
    string PackageId,
    string Channel,
    string RuntimeIdentifier);

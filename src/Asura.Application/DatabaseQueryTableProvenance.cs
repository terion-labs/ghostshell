namespace Asura.Application;

/// <summary>
/// The catalog object and metadata proven to describe an exact raw-query result.
/// </summary>
public sealed record DatabaseQueryTableProvenance(
    DatabaseTableDescriptor Table,
    DatabaseObjectDetails Details);

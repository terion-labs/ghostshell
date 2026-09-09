namespace Asura.Infrastructure;

internal sealed record SqliteMigration(
    int Version,
    string Name,
    string Sql,
    bool IsDestructive = false);

namespace Asura.Core;

public interface IDurableDefinition
{
    static abstract DefinitionKind Kind { get; }

    DefinitionKey Key { get; }

    int SchemaVersion { get; }

    string Name { get; }
}

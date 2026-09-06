using System.Text.Json.Serialization;

namespace GhostShell.Core;

public sealed record ApplicationNetworkSettings : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    public static ApplicationNetworkSettingsId DefaultId { get; } =
        new("builtin.network.default");

    public static ApplicationNetworkSettings Default { get; } = new(
        DefaultId,
        CurrentSchemaVersion,
        "Application networking",
        NetworkPolicy.Direct);

    [JsonConstructor]
    public ApplicationNetworkSettings(
        ApplicationNetworkSettingsId id,
        int schemaVersion,
        string name,
        NetworkPolicy policy)
    {
        RuntimeId.Require(id.Value, nameof(id));
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, null);
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = RuntimeId.Require(name, nameof(name)).Trim();
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public static DefinitionKind Kind => DefinitionKind.ApplicationNetworkSettings;

    public ApplicationNetworkSettingsId Id { get; }

    public int SchemaVersion { get; }

    public string Name { get; }

    public NetworkPolicy Policy { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);
}

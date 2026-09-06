using System.Text.Json.Serialization;

namespace GhostShell.Core;

/// <summary>
/// A reusable proxy or VPN definition. Secret material remains in the secret vault and
/// is referenced only by opaque identifiers.
/// </summary>
public sealed record NetworkConnectionProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public NetworkConnectionProfile(
        NetworkConnectionId id,
        int schemaVersion,
        string name,
        NetworkConnectionConfiguration configuration)
    {
        RuntimeId.Require(id.Value, nameof(id));
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, null);
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = RuntimeId.Require(name, nameof(name)).Trim();
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public static DefinitionKind Kind => DefinitionKind.NetworkConnection;

    public NetworkConnectionId Id { get; }

    public int SchemaVersion { get; }

    public string Name { get; }

    public NetworkConnectionConfiguration Configuration { get; }

    [JsonIgnore]
    public NetworkConnectionKind ConnectionKind => Configuration.Kind;

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);
}

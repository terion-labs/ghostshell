using System.Text.Json.Serialization;

namespace Asura.Core;

/// <summary>
/// Durable file-provider configuration. Authentication material is represented only by opaque
/// secret references and is resolved inside the provider adapter at execution time.
/// </summary>
public sealed record FileProviderProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public FileProviderProfile(
        FileProviderProfileId id,
        int schemaVersion,
        string name,
        FileProviderConfiguration configuration)
    {
        RuntimeId.Require(id.Value, nameof(id));
        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, null);
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Name = RuntimeId.Require(name, nameof(name)).Trim();
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public static DefinitionKind Kind => DefinitionKind.FileProviderProfile;

    public FileProviderProfileId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    public FileProviderConfiguration Configuration { get; }

    [JsonIgnore]
    public FileProviderKind ProviderKind => Configuration.Kind;
}

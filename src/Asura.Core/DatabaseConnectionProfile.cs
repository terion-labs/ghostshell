using System.Text.Json.Serialization;

namespace Asura.Core;

public readonly record struct DatabaseConnectionProfileId
{
    [JsonConstructor]
    public DatabaseConnectionProfileId(string value) =>
        Value = RuntimeId.Require(value, nameof(value));

    public string Value { get; }

    public static DatabaseConnectionProfileId New() => new(RuntimeId.NewValue());

    public override string ToString() => Value;
}

/// <summary>
/// A saved database connection: the driver, the connection string with the
/// password stripped, an optional vault reference for that password, and an
/// optional SSH tunnel. A profile whose password is neither in the vault nor
/// in the string is asked for it at connect time.
/// </summary>
public sealed record DatabaseConnectionProfile : IDurableDefinition
{
    public const int CurrentSchemaVersion = 1;

    [JsonConstructor]
    public DatabaseConnectionProfile(
        DatabaseConnectionProfileId id,
        int schemaVersion,
        string name,
        string driverId,
        string connectionString,
        SecretRef? passwordSecret = null,
        ConnectionId? tunnelConnectionId = null,
        ConnectionProfile? inlineTunnel = null)
    {
        Id = id;
        SchemaVersion = schemaVersion;
        Name = name;
        DriverId = RuntimeId.Require(driverId, nameof(driverId));
        ConnectionString = connectionString ?? string.Empty;
        PasswordSecret = passwordSecret;
        TunnelConnectionId = tunnelConnectionId;
        InlineTunnel = inlineTunnel;
    }

    /// <summary>
    /// The id an inline tunnel is created under. Deriving it from the profile
    /// keeps it stable across edits, so the vault secret scoped to it stays
    /// resolvable.
    /// </summary>
    public static ConnectionId InlineTunnelId(DatabaseConnectionProfileId profileId) =>
        new($"db-tunnel-{profileId.Value}");

    public static DefinitionKind Kind => DefinitionKind.DatabaseConnection;

    public DatabaseConnectionProfileId Id { get; }

    [JsonIgnore]
    public DefinitionKey Key => new(Kind, Id.Value);

    public int SchemaVersion { get; }

    public string Name { get; }

    /// <summary>The durable driver id ("postgres", "sqlite", …).</summary>
    public string DriverId { get; }

    /// <summary>The connection string without its password value.</summary>
    public string ConnectionString { get; }

    /// <summary>The vault entry holding the password, when stored.</summary>
    public SecretRef? PasswordSecret { get; }

    /// <summary>A saved SSH connection this profile tunnels through.</summary>
    public ConnectionId? TunnelConnectionId { get; }

    /// <summary>
    /// A tunnel that lives only inside this profile — for the database that
    /// has its own bastion, not worth a saved terminal connection. Mutually
    /// exclusive with <see cref="TunnelConnectionId"/>.
    /// </summary>
    public ConnectionProfile? InlineTunnel { get; }

    /// <summary>Resolvable tunnel intent: the saved reference wins by validation.</summary>
    [JsonIgnore]
    public bool HasTunnel => TunnelConnectionId is not null || InlineTunnel is not null;

    public DefinitionValidationResult Validate()
    {
        List<DefinitionValidationIssue> issues = [];
        if (string.IsNullOrWhiteSpace(Name))
        {
            issues.Add(new(
                DefinitionValidationCode.Required,
                "A saved database connection needs a name.",
                Id.Value));
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "A saved database connection needs a connection string.",
                Id.Value));
        }

        if (TunnelConnectionId is not null && InlineTunnel is not null)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "A database connection tunnels through a saved connection or its own — not both.",
                Id.Value));
        }

        if (InlineTunnel is { } inline && inline.Endpoint is not ConnectionEndpoint.Ssh)
        {
            issues.Add(new(
                DefinitionValidationCode.InvalidEntry,
                "An inline database tunnel must be an SSH endpoint.",
                Id.Value));
        }

        return new(issues);
    }
}

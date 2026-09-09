using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Trusted material used only while a Database Viewer session is being opened.
/// The connection string is never projected into session metadata, graph state,
/// agent results, diagnostics, or this type's string representation.
/// </summary>
public sealed class DatabaseSessionTarget
{
    private readonly string _connectionString;

    public DatabaseSessionTarget(
        string driverId,
        string connectionString,
        string bindingId,
        long bindingRevision,
        ConnectionProfile? tunnel = null,
        SecretRef? credentialReference = null)
    {
        DriverId = RequireBounded(driverId, nameof(driverId), 256);
        _connectionString = connectionString
            ?? throw new ArgumentNullException(nameof(connectionString));
        BindingId = RequireBounded(bindingId, nameof(bindingId), 256);
        ArgumentOutOfRangeException.ThrowIfNegative(bindingRevision);
        BindingRevision = bindingRevision;
        Tunnel = tunnel;
        CredentialReference = credentialReference;
    }

    public string DriverId { get; }

    /// <summary>
    /// Stable opaque identity of the connection definition or one ad-hoc panel
    /// binding. It contains no endpoint or credential material.
    /// </summary>
    public string BindingId { get; }

    public long BindingRevision { get; }

    public ConnectionProfile? Tunnel { get; }

    /// <summary>
    /// Opaque reference identifying the credential used to resolve the
    /// connection material, when the panel was opened from a saved profile.
    /// The session never resolves or returns the secret through agent tools.
    /// </summary>
    public SecretRef? CredentialReference { get; }

    public DatabaseSessionBinding Binding => new(
        DriverId,
        BindingId,
        BindingRevision,
        string.Equals(DriverId, RedisDatabase.DriverId, StringComparison.Ordinal)
            ? DatabasePanelBackend.Redis
            : DatabasePanelBackend.Relational);

    public override string ToString() =>
        $"Database session {BindingId} revision {BindingRevision}";

    /// <summary>
    /// Lets the trusted database adapter consume sensitive connection material
    /// without exposing it as a serializable property. Callers must not retain,
    /// log, or copy the supplied value outside the hosted session.
    /// </summary>
    public Task<TResult> UseConnectionStringAsync<TResult>(
        Func<string, Task<TResult>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation(_connectionString);
    }

    private static string RequireBounded(
        string value,
        string parameterName,
        int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A database session identity must be bounded and printable.",
                parameterName);
        }

        return value;
    }
}

public enum DatabasePanelBackend
{
    Relational,
    Redis,
}

public sealed record DatabaseSessionBinding(
    string DriverId,
    string BindingId,
    long BindingRevision,
    DatabasePanelBackend Backend);

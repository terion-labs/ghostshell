using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Immutable Docker connection binding admitted to a hosted panel. Connection
/// authentication contains opaque secret references only; session snapshots and
/// read results expose neither authentication nor endpoint details.
/// </summary>
public sealed class DockerSessionTarget
{
    public DockerSessionTarget(
        ConnectionProfile connection,
        long bindingRevision)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        if (connection.Endpoint is not (ConnectionEndpoint.Local or ConnectionEndpoint.Ssh))
        {
            throw new ArgumentException(
                "Docker sessions support local and SSH connections only.",
                nameof(connection));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(bindingRevision);
        BindingRevision = bindingRevision;
    }

    /// <summary>
    /// Trusted infrastructure input. Consumers must never project or serialize
    /// this profile through the hosted session or an agent-facing result.
    /// </summary>
    public ConnectionProfile Connection { get; }

    public long BindingRevision { get; }

    public DockerSessionBinding Binding => new(
        Connection.Id,
        BindingRevision,
        Connection.ConnectionKind);

    public override string ToString() =>
        $"Docker session {Connection.Id.Value} revision {BindingRevision}";
}

public sealed record DockerSessionBinding(
    ConnectionId ConnectionId,
    long BindingRevision,
    ConnectionKind ConnectionKind);

public readonly record struct DockerEngineGeneration
{
    public DockerEngineGeneration(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128
            || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= 'A' and <= 'Z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'))
        {
            throw new ArgumentException(
                "A Docker engine generation must be an opaque token.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static DockerEngineGeneration New() =>
        new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}

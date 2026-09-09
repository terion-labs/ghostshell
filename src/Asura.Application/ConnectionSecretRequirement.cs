using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Identifies secret material that a connection executor must consume inside its trusted adapter boundary.
/// It never contains the secret value.
/// </summary>
public sealed record ConnectionSecretRequirement
{
    public ConnectionSecretRequirement(
        ConnectionSecretRole role,
        SecretRef reference,
        string? environmentVariableName = null)
    {
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "The secret role is invalid.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reference.Value);
        if (role == ConnectionSecretRole.EnvironmentVariable)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(environmentVariableName);
        }
        else if (environmentVariableName is not null)
        {
            throw new ArgumentException(
                "Only environment-variable requirements may name an environment variable.",
                nameof(environmentVariableName));
        }

        Role = role;
        Reference = reference;
        EnvironmentVariableName = environmentVariableName;
    }

    public ConnectionSecretRole Role { get; }

    public SecretRef Reference { get; }

    public string? EnvironmentVariableName { get; }
}

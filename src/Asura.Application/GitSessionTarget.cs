using System.Security.Cryptography;
using System.Text;
using Asura.Core;
using Asura.Git;

namespace Asura.Application;

public readonly record struct GitRepositoryIdentity
{
    public GitRepositoryIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A Git repository identity must be a lowercase SHA-256 digest.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Trusted immutable binding for one canonical repository. The path and
/// connection profile never enter session descriptors or agent results.
/// </summary>
public sealed class GitSessionTarget
{
    public GitSessionTarget(GitRepositoryHandle repository, long bindingRevision)
    {
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentOutOfRangeException.ThrowIfNegative(bindingRevision);
        if (repository.Connection.Endpoint is not ConnectionEndpoint.Local)
        {
            throw new ArgumentException(
                "Governed Git sessions currently support local connections only.",
                nameof(repository));
        }

        BindingRevision = bindingRevision;
        Identity = CreateIdentity(repository);
    }

    public GitRepositoryHandle Repository { get; }

    public long BindingRevision { get; }

    public GitRepositoryIdentity Identity { get; }

    public GitSessionBinding Binding => new(
        Repository.Connection.Id,
        BindingRevision,
        Repository.Connection.ConnectionKind,
        Identity);

    private static GitRepositoryIdentity CreateIdentity(GitRepositoryHandle repository)
    {
        var material = string.Join(
            '\0',
            repository.Connection.Id.Value,
            repository.WorkingTreeRoot,
            repository.RunAsUser ?? string.Empty);
        return new GitRepositoryIdentity(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
                .ToLowerInvariant());
    }
}

public sealed record GitSessionBinding(
    ConnectionId ConnectionId,
    long BindingRevision,
    ConnectionKind ConnectionKind,
    GitRepositoryIdentity RepositoryIdentity);

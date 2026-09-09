using System.Security.Cryptography;
using Asura.Application;

namespace Asura.Docker;

internal sealed class DockerContainerRevisionPool
{
    private const int MaximumLeases = DockerPanelSession.MaximumResourcesPerKind;
    private readonly object _gate = new();
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);

    public DockerContainerRevision Mint(
        DockerEngineGeneration engineGeneration,
        DockerContainerSummary container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var revision = new DockerContainerRevision(token);
        var lease = new Lease(
            engineGeneration,
            Snapshot.From(container));
        lock (_gate)
        {
            if (_leases.Count >= MaximumLeases)
            {
                _leases.Clear();
            }

            _leases.Add(token, lease);
        }

        return revision;
    }

    public bool TryClaim(
        DockerContainerRevision revision,
        DockerEngineGeneration engineGeneration,
        string containerId,
        out Snapshot snapshot)
    {
        lock (_gate)
        {
            if (!_leases.Remove(revision.Value, out var lease)
                || lease.EngineGeneration != engineGeneration
                || !string.Equals(lease.Container.Id, containerId, StringComparison.Ordinal))
            {
                snapshot = default!;
                return false;
            }

            snapshot = lease.Container;
            return true;
        }
    }

    internal sealed record Snapshot(
        string Id,
        string Image,
        string State,
        string? ComposeProject,
        string? ComposeService)
    {
        public static Snapshot From(DockerContainerSummary value) => new(
            value.Id,
            value.Image,
            NormalizeState(value.State),
            value.ComposeProject,
            value.ComposeService);

        public bool Matches(DockerContainerSummary value) =>
            this == From(value);

        private static string NormalizeState(string value) =>
            value.Trim().ToLowerInvariant();
    }

    private sealed record Lease(
        DockerEngineGeneration EngineGeneration,
        Snapshot Container);
}

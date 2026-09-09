using System.Security.Cryptography;
using Asura.Application;

namespace Asura.Docker;

internal sealed class DockerResourceLeasePool
{
    private const int MaximumRetainedReferences = 4_096;
    private readonly object _gate = new();
    private readonly Dictionary<string, DockerResourceReference> _byReference =
        new(StringComparer.Ordinal);
    private readonly Dictionary<ResourceIdentity, string> _byIdentity = [];
    private readonly Queue<string> _leaseOrder = [];

    public DockerResourceReferenceId Lease(DockerResourceReference resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var identity = new ResourceIdentity(resource.Kind, resource.Id);
        lock (_gate)
        {
            if (_byIdentity.TryGetValue(identity, out var existing))
            {
                _byReference[existing] = resource;
                return new DockerResourceReferenceId(existing);
            }

            string reference;
            do
            {
                reference = Convert.ToBase64String(
                        RandomNumberGenerator.GetBytes(24))
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');
            }
            while (_byReference.ContainsKey(reference));

            while (_byReference.Count >= MaximumRetainedReferences)
            {
                var expiredReference = _leaseOrder.Dequeue();
                if (_byReference.Remove(expiredReference, out var expired))
                {
                    _byIdentity.Remove(new ResourceIdentity(expired.Kind, expired.Id));
                }
            }

            _byIdentity.Add(identity, reference);
            _byReference.Add(reference, resource);
            _leaseOrder.Enqueue(reference);
            return new DockerResourceReferenceId(reference);
        }
    }

    public bool TryResolve(
        DockerResourceReferenceId reference,
        out DockerResourceReference? resource)
    {
        lock (_gate)
        {
            return _byReference.TryGetValue(reference.Value, out resource);
        }
    }

    private sealed record ResourceIdentity(DockerResourceKind Kind, string Id);
}

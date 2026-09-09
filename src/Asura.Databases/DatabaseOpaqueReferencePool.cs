using System.Security.Cryptography;

namespace Asura.Databases;

internal sealed class DatabaseOpaqueReferencePool<T>
    where T : notnull
{
    private const int MaximumRetainedReferences = 4_096;
    private readonly object _gate = new();
    private readonly Dictionary<string, T> _byReference = new(StringComparer.Ordinal);
    private readonly Dictionary<T, string> _byValue;
    private readonly Queue<string> _leaseOrder = [];

    public DatabaseOpaqueReferencePool(IEqualityComparer<T>? comparer = null)
    {
        _byValue = new Dictionary<T, string>(comparer);
    }

    public string Lease(T value)
    {
        lock (_gate)
        {
            if (_byValue.TryGetValue(value, out var existing))
            {
                return existing;
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
                    _byValue.Remove(expired);
                }
            }

            _byReference.Add(reference, value);
            _byValue.Add(value, reference);
            _leaseOrder.Enqueue(reference);
            return reference;
        }
    }

    public bool TryResolve(string reference, out T? value)
    {
        lock (_gate)
        {
            return _byReference.TryGetValue(reference, out value);
        }
    }
}

namespace Asura.Application;

public sealed record CapabilitySet
{
    public static CapabilitySet Empty { get; } = new([]);

    public CapabilitySet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<string> Values { get; }

    public bool Contains(string capability) =>
        Values.Contains(capability, StringComparer.Ordinal);

    public CapabilitySet Intersect(CapabilitySet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new CapabilitySet(Values.Intersect(other.Values, StringComparer.Ordinal));
    }
}

namespace Asura.Packaging;

internal static class PackagingCommandParser
{
    public static IReadOnlyDictionary<string, string> Parse(
        IReadOnlyList<string> arguments,
        IReadOnlySet<string> allowedOptions)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count;)
        {
            var name = arguments[index++];
            if (!allowedOptions.Contains(name))
            {
                throw new PackagingUsageException($"Unknown option {name}.");
            }

            if (index >= arguments.Count)
            {
                throw new PackagingUsageException($"Option {name} requires a value.");
            }

            var value = arguments[index++];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new PackagingUsageException($"Option {name} cannot be empty.");
            }

            if (!values.TryAdd(name, value))
            {
                throw new PackagingUsageException(
                    $"Option {name} was supplied more than once.");
            }
        }

        return values;
    }

    public static string Required(
        IReadOnlyDictionary<string, string> values,
        string name) =>
        values.TryGetValue(name, out var value)
            ? value
            : throw new PackagingUsageException($"{name} is required.");
}

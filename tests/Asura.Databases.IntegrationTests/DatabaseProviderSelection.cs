using Asura.Application;

namespace Asura.Databases.IntegrationTests;

internal static class DatabaseProviderSelection
{
    public const string ProvidersVariable = "ASURA_DATABASE_INTEGRATION_PROVIDERS";

    public static IEnumerable<object[]> SelectedProviderIds()
    {
        var requested = ParseRequestedProviders();
        foreach (var provider in DatabaseProviderCatalog.All)
        {
            if (requested is null || requested.Contains(provider.Id))
            {
                yield return [provider.Id];
            }
        }
    }

    private static IReadOnlySet<string>? ParseRequestedProviders()
    {
        var value = Environment.GetEnvironmentVariable(ProvidersVariable);
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var selected = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var known = DatabaseProviderCatalog.All
            .Select(provider => provider.Id)
            .Append(RedisDatabase.DriverId)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = selected.Where(provider => !known.Contains(provider)).ToArray();
        if (unknown.Length > 0)
        {
            throw new InvalidOperationException(
                $"Unknown database integration provider(s): {string.Join(", ", unknown)}. "
                + $"Known providers: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
        }

        return selected;
    }
}

namespace Asura.Databases.IntegrationTests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RedisIntegrationFactAttribute : FactAttribute
{
    public RedisIntegrationFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(DatabaseIntegrationTheoryAttribute.EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {DatabaseIntegrationTheoryAttribute.EnableVariable}=1 or run scripts/test-database-viewer-integration.sh redis.";
            return;
        }

        var requested = Environment.GetEnvironmentVariable(DatabaseProviderSelection.ProvidersVariable);
        if (!string.IsNullOrWhiteSpace(requested)
            && !string.Equals(requested.Trim(), "all", StringComparison.OrdinalIgnoreCase)
            && !requested.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(Asura.Application.RedisDatabase.DriverId, StringComparer.Ordinal))
        {
            Skip = "Redis was not selected for this integration run.";
        }
    }
}

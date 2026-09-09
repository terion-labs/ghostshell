namespace Asura.Databases.IntegrationTests;

/// <summary>
/// Keeps Docker and heavyweight database images out of the ordinary repository
/// gate while leaving the project in the solution so it is always compiled and
/// formatted. The dedicated script sets the opt-in variable before invoking it.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class DatabaseIntegrationTheoryAttribute : TheoryAttribute
{
    public const string EnableVariable = "ASURA_RUN_DATABASE_INTEGRATION";

    public DatabaseIntegrationTheoryAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(EnableVariable),
                "1",
                StringComparison.Ordinal))
        {
            Skip = $"Set {EnableVariable}=1 or run scripts/test-database-viewer-integration.sh.";
        }
    }
}

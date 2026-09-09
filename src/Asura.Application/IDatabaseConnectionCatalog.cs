using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Connection-definition operations shared by relational and non-relational
/// database runtimes. It deliberately excludes query operations: a connection
/// editor can describe Redis without pretending Redis is an ADO.NET driver.
/// </summary>
public interface IDatabaseConnectionCatalog
{
    IReadOnlyList<DatabaseDriverDescriptor> Drivers { get; }

    /// <summary>Strict local parsing only; never opens a connection or resolves credentials.</summary>
    bool IsConnectionStringValid(string driverId, string connectionString) => false;

    Task<DatabaseSessionInfo> DescribeSessionAsync(
        string driverId,
        string connectionString,
        ConnectionProfile? tunnel,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DatabaseSessionInfo());

    DatabaseConnectionDetails ParseConnectionDetails(
        string driverId,
        string connectionString);

    string BuildConnectionString(
        string driverId,
        DatabaseConnectionDetails details);
}

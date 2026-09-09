using System.Xml.Linq;

namespace Asura.Architecture.Tests;

public sealed class SqlClientTestDependencyTests
{
    [Fact]
    public void ProductionUsesPublishedSqlClientPackageWithoutSourceFork()
    {
        var project = XDocument.Load(Path.Combine(FindRepositoryRoot(),
            "src/Asura.Databases/Asura.Databases.csproj"));
        var package = Assert.Single(project.Descendants("PackageReference"),
            reference => string.Equals((string?)reference.Attribute("Include"), "Microsoft.Data.SqlClient", StringComparison.Ordinal));
        Assert.Null(package.Attribute("Version"));
        Assert.DoesNotContain(project.Descendants("ProjectReference"), reference =>
            ((string?)reference.Attribute("Include"))?.Contains("sqlclient/upstream", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Null(typeof(Microsoft.Data.SqlClient.SqlConnection).GetProperty("TcpTransport"));
    }

    [Fact]
    public void TdsServerDependencyIsTestOnly()
    {
        var root = FindRepositoryRoot();
        var architecture = XDocument.Load(Path.Combine(root,
            "tests/Asura.Architecture.Tests/Asura.Architecture.Tests.csproj"));
        Assert.Single(architecture.Descendants("ProjectReference"), IsTestServerReference);
        foreach (var sourceDirectory in new[] { "src", "tools" })
        {
            foreach (var project in Directory.EnumerateFiles(Path.Combine(root, sourceDirectory),
                "*.csproj", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain(XDocument.Load(project).Descendants("ProjectReference"), IsTestServerReference);
            }
        }
        var support = XDocument.Load(Path.Combine(root, "vendor/sqlclient/tests/Tds.TestSupport.csproj"));
        Assert.Empty(support.Descendants("PackageReference"));
        Assert.Empty(support.Descendants("ProjectReference"));
        Assert.Equal("false", Assert.Single(support.Descendants("IsPackable")).Value);
    }

    private static bool IsTestServerReference(XElement reference) =>
        ((string?)reference.Attribute("Include"))?.EndsWith("Tds.TestSupport.csproj", StringComparison.Ordinal) == true;

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}

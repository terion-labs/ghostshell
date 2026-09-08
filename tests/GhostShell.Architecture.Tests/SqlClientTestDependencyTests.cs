using System.Xml.Linq;

namespace GhostShell.Architecture.Tests;

public sealed class SqlClientTestDependencyTests
{
    [Fact]
    public void TdsServerDependencyIsTestOnly()
    {
        var root = FindRepositoryRoot();
        var architecture = XDocument.Load(Path.Combine(root,
            "tests/GhostShell.Architecture.Tests/GhostShell.Architecture.Tests.csproj"));
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
            if (File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}

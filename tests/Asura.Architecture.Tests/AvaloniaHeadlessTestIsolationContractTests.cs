namespace Asura.Architecture.Tests;

public sealed class AvaloniaHeadlessTestIsolationContractTests
{
    [Fact]
    public void Every_headless_session_test_uses_the_serial_Avalonia_collection()
    {
        var testRoot = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "Asura.App.Tests");
        var offenders = Directory
            .EnumerateFiles(testRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .Where(file => file.Source.Contains(
                "HeadlessUnitTestSession.StartNew",
                StringComparison.Ordinal))
            .Where(file => !file.Source.Contains(
                "[Collection(AvaloniaUiCollection.Name)]",
                StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file.Path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Avalonia headless sessions reset a process-wide dispatcher and must not "
            + "run in parallel. Missing Avalonia UI collection: "
            + string.Join(", ", offenders));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Unable to locate the Asura repository root.");
    }
}

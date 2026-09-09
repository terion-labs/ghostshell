namespace Asura.Architecture.Tests;

public sealed class MacOsObjectiveCNamespaceTests
{
    [Fact]
    public void Macos_bundles_namespace_Avalonias_Chromium_file_dialog_class()
    {
        var helper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "namespace-avalonia-native-macos.sh"));
        var developmentRunner = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "run-macos-development.sh"));
        var packageScript = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "package-macos.sh"));

        Assert.Contains("original_class=\"ExtensionDropdownHandler\"", helper, StringComparison.Ordinal);
        Assert.Contains("namespaced_class=\"AvnFileTypeDropdownClass\"", helper, StringComparison.Ordinal);
        Assert.Contains("expected_occurrences=32", helper, StringComparison.Ordinal);
        Assert.Contains("len(original) != len(namespaced)", helper, StringComparison.Ordinal);
        Assert.Contains("--preserve-metadata=identifier,requirements,flags", helper, StringComparison.Ordinal);
        Assert.Contains("codesign --verify --strict", helper, StringComparison.Ordinal);

        AssertNamespacesCopiedPayloadBefore(
            developmentRunner,
            "Chromium Embedded Framework.framework");
        AssertNamespacesCopiedPayloadBefore(
            packageScript,
            "--publish \"${publish_dir}\"");
        Assert.Contains(
            "${publish_dir}/libAvaloniaNative.dylib",
            packageScript,
            StringComparison.Ordinal);
    }

    private static void AssertNamespacesCopiedPayloadBefore(
        string script,
        string laterMarker)
    {
        var invocation = script.IndexOf(
            "\n\"${namespace_avalonia_native}\" \\",
            StringComparison.Ordinal);
        var later = script.IndexOf(
            laterMarker,
            invocation + 1,
            StringComparison.Ordinal);

        Assert.True(invocation >= 0, "The namespace helper is not invoked.");
        Assert.True(later > invocation, "The namespace helper runs too late.");
    }

    private static readonly string RepositoryRoot = FindRepositoryRoot();

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

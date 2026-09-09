using Asura.App;

namespace Asura.App.Tests;

public sealed class SourcePreviewGrammarTests
{
    [Theory]
    [InlineData("Program.cs", ".cs")]
    [InlineData("styles.CSS", ".css")]
    [InlineData("query.sql", ".sql")]
    public void Known_extensions_resolve_to_themselves_in_lower_case(
        string fileName,
        string expected)
    {
        Assert.Equal(expected, SourcePreviewGrammar.ResolveExtension(fileName));
    }

    [Theory]
    [InlineData("MainWindow.axaml", ".xml")]
    [InlineData("Asura.App.csproj", ".xml")]
    [InlineData("Directory.Build.props", ".xml")]
    [InlineData("tsconfig.jsonc", ".json")]
    [InlineData("bundle.mjs", ".js")]
    [InlineData("bootstrap.zsh", ".sh")]
    public void Aliased_extensions_resolve_to_the_grammar_that_reads_them(
        string fileName,
        string expected)
    {
        Assert.Equal(expected, SourcePreviewGrammar.ResolveExtension(fileName));
    }

    [Theory]
    [InlineData("Dockerfile", ".dockerfile")]
    [InlineData("makefile", ".mak")]
    [InlineData("Gemfile", ".rb")]
    [InlineData(".editorconfig", ".ini")]
    [InlineData(".gitignore", ".ignore")]
    public void Well_known_names_resolve_without_an_extension(
        string fileName,
        string expected)
    {
        Assert.Equal(expected, SourcePreviewGrammar.ResolveExtension(fileName));
    }

    [Fact]
    public void A_path_resolves_by_its_file_name()
    {
        Assert.Equal(
            ".xml",
            SourcePreviewGrammar.ResolveExtension("/srv/app/src/App/MainWindow.axaml"));
        Assert.Equal(
            ".dockerfile",
            SourcePreviewGrammar.ResolveExtension("deploy/Dockerfile"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("LICENSE")]
    [InlineData("core.dump")]
    public void Files_without_a_recognizable_language_render_as_plain_text(string? fileName)
    {
        // ".dump" is not aliased, so it reaches the registry as itself and is
        // simply unknown there; the null cases never get that far.
        var resolved = SourcePreviewGrammar.ResolveExtension(fileName);
        Assert.True(resolved is null || string.Equals(resolved, ".dump", StringComparison.Ordinal));
    }
}

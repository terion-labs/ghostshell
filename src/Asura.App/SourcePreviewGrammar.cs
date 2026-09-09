namespace Asura.App;

/// <summary>
/// Which grammar a previewed file should be highlighted with, expressed as the
/// file extension the TextMate registry is asked about.
///
/// This is policy, not presentation: the registry knows what ".xml" means, but
/// not that Asura's own ".axaml" is XML, and it looks up by extension only,
/// so extensionless files with well-known names would otherwise fall back to
/// plain text.
/// </summary>
public static class SourcePreviewGrammar
{
    private static readonly Dictionary<string, string> ExtensionAliases = new(
        StringComparer.OrdinalIgnoreCase)
    {
        [".axaml"] = ".xml",
        [".xaml"] = ".xml",
        [".csproj"] = ".xml",
        [".fsproj"] = ".xml",
        [".vbproj"] = ".xml",
        [".props"] = ".xml",
        [".targets"] = ".xml",
        [".slnx"] = ".xml",
        [".nuspec"] = ".xml",
        [".plist"] = ".xml",
        [".resx"] = ".xml",
        [".xshd"] = ".xml",
        [".jsonc"] = ".json",
        [".webmanifest"] = ".json",
        [".mjs"] = ".js",
        [".cjs"] = ".js",
        [".zsh"] = ".sh",
        [".bash"] = ".sh",
        [".fish"] = ".sh",
        [".gitignore"] = ".ignore",
        [".dockerignore"] = ".ignore",
    };

    private static readonly Dictionary<string, string> WellKnownFileNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ["dockerfile"] = ".dockerfile",
        ["containerfile"] = ".dockerfile",
        ["makefile"] = ".mak",
        ["gnumakefile"] = ".mak",
        ["justfile"] = ".mak",
        ["cmakelists.txt"] = ".cmake",
        ["gemfile"] = ".rb",
        ["rakefile"] = ".rb",
        ["podfile"] = ".rb",
        ["brewfile"] = ".rb",
        [".gitignore"] = ".ignore",
        [".dockerignore"] = ".ignore",
        [".editorconfig"] = ".ini",
        [".zshrc"] = ".sh",
        [".bashrc"] = ".sh",
        [".bash_profile"] = ".sh",
        [".profile"] = ".sh",
    };

    /// <summary>
    /// The extension to look a grammar up by, or null when the file has no
    /// recognizable language and should render as plain text.
    /// </summary>
    public static string? ResolveExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // A path is accepted as readily as a bare name: the preview title is a
        // file name today, but a caller passing a full path should not silently
        // lose highlighting.
        var name = Path.GetFileName(fileName.Trim());
        if (name.Length == 0)
        {
            return null;
        }

        if (WellKnownFileNames.TryGetValue(name, out var wellKnown))
        {
            return wellKnown;
        }

        var extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension))
        {
            return null;
        }

        return ExtensionAliases.TryGetValue(extension, out var alias)
            ? alias
            : extension.ToLowerInvariant();
    }
}

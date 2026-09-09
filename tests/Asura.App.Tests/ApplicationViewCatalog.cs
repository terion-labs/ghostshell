using System.Xml.Linq;

namespace Asura.Testing;

internal sealed class ApplicationViewCatalog
{
    private readonly IReadOnlyList<ApplicationViewDocument> _documents;
    private readonly IReadOnlyList<(string Path, string Source)> _codeBehindDocuments;

    private ApplicationViewCatalog(
        string repositoryRoot,
        IReadOnlyList<ApplicationViewDocument> documents,
        IReadOnlyList<(string Path, string Source)> codeBehindDocuments)
    {
        RepositoryRoot = repositoryRoot;
        _documents = documents;
        _codeBehindDocuments = codeBehindDocuments;
    }

    public string RepositoryRoot { get; }

    public static ApplicationViewCatalog Load()
    {
        var repositoryRoot = FindRepositoryRoot();
        var viewsRoot = Path.Combine(
            repositoryRoot,
            "src",
            "Asura.App",
            "Views");
        var documents = Directory
            .EnumerateFiles(viewsRoot, "*.axaml", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => new ApplicationViewDocument(
                path,
                XDocument.Load(path, LoadOptions.SetLineInfo)))
            .ToArray();
        var codeBehindDocuments = Directory
            .EnumerateFiles(viewsRoot, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => (Path: path, Source: File.ReadAllText(path)))
            .ToArray();

        Assert.NotEmpty(documents);
        return new ApplicationViewCatalog(
            repositoryRoot,
            documents,
            codeBehindDocuments);
    }

    public OwnedApplicationViewElement FindUniqueNamedElement(string name)
    {
        var matches = _documents
            .SelectMany(document => Elements(document)
                .Where(element => string.Equals(
                    AttributeValue(element, "Name"),
                    name,
                    StringComparison.Ordinal))
                .Select(element => new OwnedApplicationViewElement(
                    document,
                    element)))
            .ToArray();

        Assert.True(
            matches.Length == 1,
            DescribeMatchCount(
                $"named element '{name}'",
                matches.Select(match => match.Owner.Path)));
        return matches[0];
    }

    public ApplicationViewDocument FindUniqueOwnerDocument(
        string description,
        Func<XElement, bool> predicate)
    {
        var matches = _documents
            .Where(document => Elements(document).Any(predicate))
            .ToArray();

        Assert.True(
            matches.Length == 1,
            DescribeMatchCount(
                description,
                matches.Select(match => match.Path)));
        return matches[0];
    }

    public string FindUniqueCodeBehindSourceContaining(string text)
    {
        var matches = _codeBehindDocuments
            .Where(document => document.Source.Contains(
                text,
                StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            matches.Length == 1,
            DescribeMatchCount(
                $"code-behind text '{text}'",
                matches.Select(match => match.Path)));
        return matches[0].Source;
    }

    public string FindPartialClassSources(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        var filePrefix = $"{typeName}.";
        var declaration = $"partial class {typeName}";
        var matches = _codeBehindDocuments
            .Where(document =>
                Path.GetFileName(document.Path).StartsWith(
                    filePrefix,
                    StringComparison.Ordinal)
                && document.Source.Contains(
                    declaration,
                    StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(matches);
        return string.Join(
            Environment.NewLine,
            matches.Select(document =>
                $"// {Path.GetRelativePath(RepositoryRoot, document.Path)}"
                + Environment.NewLine
                + document.Source));
    }

    private static IEnumerable<XElement> Elements(
        ApplicationViewDocument document) =>
        document.Document.Root?.DescendantsAndSelf() ?? [];

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;

    private string DescribeMatchCount(
        string description,
        IEnumerable<string> paths)
    {
        var relativePaths = paths
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();
        return $"Expected exactly one application view to own {description}, "
            + $"but found {relativePaths.Length}: "
            + string.Join(", ", relativePaths);
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
            "Could not locate the Asura repository root.");
    }
}

internal sealed record ApplicationViewDocument(
    string Path,
    XDocument Document);

internal sealed record OwnedApplicationViewElement(
    ApplicationViewDocument Owner,
    XElement Element);

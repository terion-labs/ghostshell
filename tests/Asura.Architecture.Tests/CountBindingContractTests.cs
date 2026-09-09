using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Asura.Architecture.Tests;

/// <summary>
/// Guards a binding failure that is invisible rather than loud.
///
/// Avalonia resolves a binding path by reflecting over the value's runtime type.
/// An <c>IReadOnlyList&lt;T&gt;</c> backed by an array has no public <c>Count</c>
/// — only <c>Length</c>, plus an explicit interface implementation reflection
/// does not see — so <c>{Binding Something.Count}</c> renders an empty pill and
/// no error. The layout designer's panel count shipped that way.
///
/// The rule: a view may only bind <c>.Count</c> through a property whose declared
/// type exposes it publicly, which in this application means the observable
/// collections. Anything else must expose its own count property.
/// </summary>
public sealed partial class CountBindingContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>
    /// Collections whose declared type is an ObservableCollection or a
    /// ReadOnlyObservableCollection over one. Both publish <c>Count</c> on the
    /// runtime type and raise change notifications for it, so a binding resolves
    /// and stays live.
    /// </summary>
    private static readonly string[] ObservableCollectionPaths =
    [
        "Connections",
        "Screens",
        "Workspaces",
        "ConnectionsPreview",
        "ScreensPreview",
        "IconChoices",
        "Entries",
        "Panels",
        "RecentSessions",
    ];

    [Fact]
    public void Count_is_only_bound_through_collections_that_publish_it()
    {
        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(RepositoryRoot, "src", "Asura.App"),
                "*.axaml",
                SearchOption.AllDirectories)
            .SelectMany(file => CountBindings()
                .Matches(File.ReadAllText(file))
                .Select(match => (File: file, Path: match.Groups["path"].Value)))
            .Where(binding => !IsResolvable(binding.Path))
            .Select(binding =>
                $"{Path.GetRelativePath(RepositoryRoot, binding.File)}: {binding.Path}.Count")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These bindings resolve .Count over a type that may not publish it, which renders "
            + "empty with no error. Expose an explicit count property instead:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsResolvable(string path)
    {
        // Only the final segment matters: it names the collection being counted.
        var collection = path.Split('.').Last();
        return ObservableCollectionPaths.Contains(collection, StringComparer.Ordinal);
    }

    [GeneratedRegex(
        @"\{Binding (?<path>[A-Za-z_][A-Za-z0-9_.]*)\.Count[,}]",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex CountBindings();

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

        throw new DirectoryNotFoundException("Unable to locate the Asura repository root.");
    }
}

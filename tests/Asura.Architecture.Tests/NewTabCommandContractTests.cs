using System.Text.RegularExpressions;
using Asura.Testing;

namespace Asura.Architecture.Tests;

/// <summary>
/// A new tab asks what to open.
///
/// The command is called "New tab", the plus beside the tabs opens a tab that
/// asks, and the keyboard opened a local terminal already running — one of the
/// answers rather than the question, with no way to reach the others except to
/// close it again. The two ways of asking for the same thing have to agree, and
/// the shorter one is the one people press.
/// </summary>
public sealed class NewTabCommandContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void The_new_tab_command_opens_a_tab_that_asks_what_to_open()
    {
        foreach (var body in NewTabHandlerBodies())
        {
            Assert.Contains("ShowNewItemLauncherAsync", body, StringComparison.Ordinal);
            Assert.DoesNotContain("AddLocalTerminalTabAsync", body, StringComparison.Ordinal);
            Assert.DoesNotContain("RequestNewTerminalAsync", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Both of them: the routed keybinding and the palette's own entry. They
    /// were one shortcut and one list item doing two different things.
    /// </summary>
    [Fact]
    public void Both_ways_of_asking_for_a_new_tab_are_wired()
    {
        Assert.Equal(2, NewTabHandlerBodies().Count);
    }

    private static IReadOnlyList<string> NewTabHandlerBodies() =>
        [.. FindNewTabHandlerBodies(
                ApplicationViews.FindPartialClassSources("MainWindow"))
            .Concat(FindNewTabHandlerBodies(File.ReadAllText(Path.Combine(
                ApplicationViews.RepositoryRoot,
                "src",
                "Asura.App",
                "ShellCommandExecutor.cs"))))];

    private static IEnumerable<string> FindNewTabHandlerBodies(string source) =>
        [.. Regex.Matches(
                source,
                @"(case ApplicationCommandActionKind\.NewTab:|command\.Id == BuiltInCommands\.NewTab\))",
                RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
                TimeSpan.FromSeconds(1))
            .Select(match => BodyAfter(source, match.Index + match.Length))
            .Where(body => body.Length > 0)];

    /// <summary>
    /// From the marker to the end of what it guards — the next case label, or
    /// the closing brace of the block it opened. Crude, and it only has to
    /// separate one arm from the next.
    /// </summary>
    private static string BodyAfter(string source, int start)
    {
        var nextCase = source.IndexOf("case ", start, StringComparison.Ordinal);
        var end = nextCase < 0 ? Math.Min(source.Length, start + 400) : nextCase;
        return source[start..end];
    }
}

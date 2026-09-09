using System.Text.RegularExpressions;
using Asura.Testing;

namespace Asura.Architecture.Tests;

/// <summary>
/// Opening a workspace must never close one.
///
/// This bug survived four fixes. Each fix was real — the client stopped
/// disposing backgrounded runtimes, the host learned to hold several workspace
/// graphs per window, recovery started registering restored workspaces — and
/// none of them helped, because the defect was one line above all of them: the
/// rail tile ran the *window* close flow before opening anything, so every
/// terminal in the window was killed and the "already open, reactivate it" path
/// underneath was never reached.
///
/// It survived because every test called the view model directly. The defect
/// lived in the view, where nothing looked. These read the view's own source,
/// which is the cheapest place to make this class of mistake impossible.
/// </summary>
public sealed class WorkspaceSwitchingContractTests
{
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    /// <summary>
    /// The close flow ends sessions. A handler that opens or activates a
    /// workspace must not reach it — no matter how convenient it is for
    /// "replace what is on screen".
    /// </summary>
    [Fact]
    public void No_workspace_open_path_runs_the_window_close_flow()
    {
        var source = ApplicationViews.FindPartialClassSources("MainWindow");

        var offenders = OpenHandlerBodies(source)
            .Where(handler => handler.Body.Contains("CloseWindowAsync", StringComparison.Ordinal))
            .Select(handler => handler.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"These handlers open a workspace and close the window's sessions on the way: "
            + $"{string.Join(", ", offenders)}. Opening is not replacing — the close flow "
            + "belongs to closing the window, and to closing a tab.");
    }

    /// <summary>
    /// The helper every open path goes through must not carry a close at all.
    /// Named separately from the sweep above so the failure names the cause
    /// rather than the ten handlers that inherit it.
    /// </summary>
    [Fact]
    public void The_shared_open_helper_does_not_close_anything()
    {
        var source = ApplicationViews.FindPartialClassSources("MainWindow");
        var helper = MethodBody(source, "OpenRuntimeWorkspaceAsync")
            ?? MethodBody(source, "ReplaceRuntimeWorkspaceAsync");

        Assert.NotNull(helper);
        Assert.DoesNotContain("CloseWindowAsync", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("RunCloseFlowAsync", helper, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every runtime workspace must be registered where the shell can find it
    /// again. A workspace assigned straight to the property is invisible to the
    /// open set, and the next assignment disposes its panels — the same session
    /// loss, arriving by a different door.
    /// </summary>
    [Fact]
    public void Every_runtime_workspace_is_registered_rather_than_assigned()
    {
        var source = File.ReadAllText(Path.Combine(
            ApplicationViews.RepositoryRoot,
            "src",
            "Asura.App",
            "ViewModels",
            "MainWindowViewModel.cs"));

        // Assignments inside the owner's own methods are the registration; every
        // other one bypasses it.
        var assignments = Regex.Matches(
                source,
                @"RuntimeWorkspace = (?<value>[^;]+);",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1))
            .Select(match => match.Groups["value"].Value.Trim())
            .Where(value => !string.Equals(value, "null", StringComparison.Ordinal))
            .ToArray();

        var registrations = OwnerMethodBodies(source)
            .Sum(body => Regex.Count(
                body,
                @"RuntimeWorkspace = [^;]+;",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)));

        Assert.True(
            assignments.Length <= registrations,
            $"{assignments.Length - registrations} runtime workspace(s) are assigned outside "
            + "ActivateRuntimeWorkspace/ReactivateRuntimeWorkspace/CloseRuntimeWorkspace. "
            + "A workspace that never enters the open set is disposed by the next switch.");
    }

    private static IEnumerable<(string Name, string Body)> OpenHandlerBodies(string source) =>
        Regex.Matches(
                source,
                @"(?<signature>(private|public|internal)[^\n;]*?(?<name>On\w*Open\w*|OpenDefaultLocalTerminalAsync)\s*\([^)]*\))",
                RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
                TimeSpan.FromSeconds(1))
            .Select(match => (
                Name: match.Groups["name"].Value,
                Body: BodyAfter(source, match.Index + match.Length)))
            .Where(handler => handler.Body.Length > 0);

    private static IEnumerable<string> OwnerMethodBodies(string source) =>
        new[]
        {
            "ActivateRuntimeWorkspace",
            "ReactivateRuntimeWorkspace",
            "CloseRuntimeWorkspace",
        }
        .Select(name => MethodBody(source, name))
        .Where(body => body is not null)
        .Select(body => body!);

    /// <summary>
    /// The body of a named method, by brace matching from its signature. Crude
    /// but honest: these tests read source precisely because the thing they
    /// guard is not reachable any other way.
    /// </summary>
    private static string? MethodBody(string source, string methodName)
    {
        // Anchored on the declaration. Without the modifier prefix this matches
        // the first *call* instead, and returns a lambda argument as the body —
        // which is how the first draft of this test passed against the very
        // code it was written to reject.
        var match = Regex.Match(
            source,
            $@"(private|public|internal|protected)[^\n;]*?\b{Regex.Escape(methodName)}\s*\([^)]*\)",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(1));
        return match.Success ? BodyAfter(source, match.Index + match.Length) : null;
    }

    private static string BodyAfter(string source, int start)
    {
        var open = source.IndexOf('{', start);
        if (open < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
            {
                return source[open..(index + 1)];
            }
        }

        return string.Empty;
    }
}

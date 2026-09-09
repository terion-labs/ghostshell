namespace Asura.Architecture.Tests;

/// <summary>
/// Saving a terminal font must update the single managed presentation used on
/// every desktop without replacing the session or losing scrollback.
/// </summary>
public sealed class TerminalLiveTypographyContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void The_session_host_exposes_the_live_profile_operation()
    {
        Assert.Contains(
            "UpdateTerminalRenderProfileAsync",
            Read("src", "Asura.Application", "ISessionHostClient.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "public async ValueTask<HostResult<bool>> UpdateTerminalRenderProfileAsync(",
            Read("src", "Asura.SessionHost", "InMemorySessionHostClient.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_managed_presentation_receives_the_live_profile()
    {
        var host = Read("src", "Asura.App", "Controls", "TerminalPresentationHost.cs");

        Assert.Contains("_presentation.RenderProfile = RenderProfile;", host, StringComparison.Ordinal);
        Assert.DoesNotContain("TerminalPresentationSelector", host, StringComparison.Ordinal);
        Assert.DoesNotContain("new TerminalSessionHost", host, StringComparison.Ordinal);
    }

    [Fact]
    public void The_managed_surface_draws_with_the_live_profile()
    {
        var surface = Read("src", "Asura.App", "Controls", "ManagedTerminalSurface.cs");

        Assert.Contains("RenderProfile", surface, StringComparison.Ordinal);
        Assert.Contains("FontFamily", surface, StringComparison.Ordinal);
        Assert.Contains("FontSize", surface, StringComparison.Ordinal);
        Assert.Contains("LineHeight", surface, StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));

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

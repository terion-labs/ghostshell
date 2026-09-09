using Asura.Core;

namespace Asura.Application.Tests;

public sealed class TerminalConnectionMetadataTests
{
    [Fact]
    public void Metadata_escapes_non_printable_text_and_is_immutable()
    {
        var metadata = new TerminalConnectionMetadata(
            "SSH: deploy@example.test\nsecondary",
            "/srv/api\tcurrent");

        Assert.Equal(@"SSH: deploy@example.test\nsecondary", metadata.ConnectionBoundary);
        Assert.Equal(@"/srv/api\tcurrent", metadata.InitialWorkingDirectory);
        Assert.DoesNotContain(metadata.ConnectionBoundary, char.IsControl);
        Assert.DoesNotContain(metadata.InitialWorkingDirectory!, char.IsControl);
    }

    [Fact]
    public void Metadata_rejects_missing_or_oversized_presentation()
    {
        Assert.Throws<ArgumentException>(() => new TerminalConnectionMetadata(" ", "/srv"));
        Assert.Throws<ArgumentException>(() => new TerminalConnectionMetadata(
            new string('h', TerminalConnectionMetadata.MaximumBoundaryBytes + 1),
            "/srv"));
        Assert.Throws<ArgumentException>(() => new TerminalConnectionMetadata(
            "Local",
            new string('w', TerminalConnectionMetadata.MaximumWorkingDirectoryBytes + 1)));
        Assert.Throws<ArgumentException>(() => new TerminalConnectionMetadata(
            "SSH: \uD800",
            "/srv"));
    }

    [Fact]
    public void Launch_profile_clone_preserves_connection_metadata()
    {
        var metadata = new TerminalConnectionMetadata(
            "Docker: desktop-linux/api",
            "/workspace");
        var launch = new TerminalLaunchRequest(
            null,
            "/usr/bin/docker",
            ["exec", "api"],
            connectionId: new ConnectionId("docker-api"),
            connectionMetadata: metadata);

        var clone = launch.WithPresentationProfiles(
            renderProfile: null,
            TerminalKeymapSnapshot.FromProfile(BuiltInKeymaps.LinuxTerminal));

        Assert.Equal(launch.ConnectionId, clone.ConnectionId);
        Assert.Same(metadata, clone.ConnectionMetadata);
        Assert.Equal(launch.Arguments, clone.Arguments);
        Assert.Equal(launch.Environment, clone.Environment);
    }

    [Fact]
    public void Live_metadata_advances_only_the_current_directory()
    {
        var initial = new TerminalSessionMetadata(
            new ConnectionId("ssh-production"),
            "SSH: deploy@production.example:22",
            "/srv/start",
            "/srv/start");

        var current = initial.WithCurrentWorkingDirectory("/srv/current");

        Assert.Equal(initial.ConnectionId, current.ConnectionId);
        Assert.Equal(initial.ConnectionBoundary, current.ConnectionBoundary);
        Assert.Equal(initial.InitialWorkingDirectory, current.InitialWorkingDirectory);
        Assert.Equal("/srv/current", current.CurrentWorkingDirectory);
        Assert.Equal("/srv/start", initial.CurrentWorkingDirectory);
    }
}

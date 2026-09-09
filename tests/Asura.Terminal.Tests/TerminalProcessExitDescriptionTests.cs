using Asura.Application;

namespace Asura.Terminal.Tests;

public sealed class TerminalProcessExitDescriptionTests
{
    [Theory]
    [InlineData(255, "The OpenSSH process exited with code 255.")]
    [InlineData(42, "The OpenSSH process exited with code 42.")]
    [InlineData(0, "The SSH session ended normally.")]
    [InlineData(null, "The SSH session ended.")]
    public void SshExitUsesOnlyLocalExitCode(
        int? exitCode,
        string expected)
    {
        var description = TerminalProcessExitDescription.Describe(
            SshLaunch(),
            exitCode);

        Assert.Equal(expected, description);
        Assert.DoesNotContain("private.example", description, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSshFailureReportsOnlyTheExitCode()
    {
        var description = TerminalProcessExitDescription.Describe(
            SshLaunch(),
            exitCode: 42);

        Assert.Equal("The OpenSSH process exited with code 42.", description);
        Assert.DoesNotContain("private failure text", description, StringComparison.Ordinal);
    }

    private static TerminalLaunchRequest SshLaunch() => new(
        workingDirectory: null,
        executable: "/usr/bin/ssh",
        connectionMetadata: new TerminalConnectionMetadata(
            "SSH: root@private.example:22",
            initialWorkingDirectory: null));
}

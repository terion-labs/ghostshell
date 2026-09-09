using System.Text;
using Asura.Desktop;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceSshCommandTests
{
    [Fact]
    public void Git_ssh_destination_is_encoded_before_entering_the_proxy_shell_command()
    {
        const string host = "host'; echo not-a-command; '";
        var arguments = WorkspaceSshCommand.RoutedArguments(
            "/Applications/Asura.app/Contents/MacOS/Asura",
            45123, host, 2222, ["git@alias", "git-upload-pack 'repository'"]);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(host)), arguments[1], StringComparison.Ordinal);
        Assert.DoesNotContain(host, arguments[1], StringComparison.Ordinal);
        Assert.Contains("ControlPath=none", arguments, StringComparer.Ordinal);
        Assert.Equal("git@alias", arguments[^2]);
        Assert.Equal("git-upload-pack 'repository'", arguments[^1]);
    }
}

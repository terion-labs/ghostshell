using Asura.Testing;

namespace Asura.Architecture.Tests;

public sealed class TerminalNotificationProtocolContractTests
{
    private static readonly string RepositoryRoot =
        ApplicationViewCatalog.Load().RepositoryRoot;

    [Fact]
    public void Terminal_notification_ingress_does_not_rewrite_application_launches()
    {
        var factory = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.Terminal",
            "GhosttyVtTerminalSessionFactory.cs"));

        Assert.Contains("shellIntegration.Launch", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("NotificationAdapter", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_package_contains_no_provider_notification_plugins_or_shims()
    {
        Assert.False(Directory.Exists(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.Desktop",
            "Resources",
            "Claude")));

        var packageInputs = new[]
        {
            Path.Combine(
                RepositoryRoot,
                "src",
                "Asura.Desktop",
                "Asura.Desktop.csproj"),
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"),
            Path.Combine(
                RepositoryRoot,
                "tools",
                "Asura.Packaging",
                "MacOsAppBundleBuilder.cs"),
        };
        var forbiddenPaths = new[]
        {
            "claude-plugins",
            "asura-cli-shims",
            "terminal-shell-integration",
        };

        foreach (var input in packageInputs)
        {
            var contents = File.ReadAllText(input);
            foreach (var forbiddenPath in forbiddenPaths)
            {
                Assert.DoesNotContain(
                    forbiddenPath,
                    contents,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

using System.Xml.Linq;

namespace Asura.Architecture.Tests;

public sealed class WorkspaceNetworkGatewayPackagingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void BuildProducesArm64AndX64GatewayArtifactsDeterministically()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "build-workspace-network-gateway.sh"));

        Assert.Contains("expected_go_version=\"go1.26.3\"", script, StringComparison.Ordinal);
        Assert.Contains("CGO_ENABLED=0", script, StringComparison.Ordinal);
        Assert.Contains("GOOS=\"${goos}\" GOARCH=\"${goarch}\" go build", script, StringComparison.Ordinal);
        Assert.Contains("-trimpath", script, StringComparison.Ordinal);
        Assert.Contains("-buildvcs=false", script, StringComparison.Ordinal);
        Assert.Contains("-ldflags=-buildid=", script, StringComparison.Ordinal);
        Assert.Contains("./cmd/workspace-network-gateway", script, StringComparison.Ordinal);
        Assert.Contains("asura-workspace-gateway-darwin-arm64", script, StringComparison.Ordinal);
        Assert.Contains("asura-workspace-gateway-linux-arm64", script, StringComparison.Ordinal);
        Assert.Contains("asura-workspace-gateway-darwin-amd64", script, StringComparison.Ordinal);
        Assert.Contains("asura-workspace-gateway-linux-amd64", script, StringComparison.Ordinal);
        Assert.Contains("workspace-network-gateway-MANIFEST.sha256", script, StringComparison.Ordinal);
        Assert.Contains("workspace-network-gateway-THIRD-PARTY-NOTICES.md", script, StringComparison.Ordinal);
        Assert.Contains("workspace-network-gateway-GO-LICENSE.txt", script, StringComparison.Ordinal);
        Assert.Contains("go version -m", script, StringComparison.Ordinal);
        Assert.Contains("go list -m -f '{{.Dir}}'", script, StringComparison.Ordinal);
        Assert.Contains("shasum -a 256 -c", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPublishCarriesOnlyHostRuntimeAndGatewayPayloads()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Asura.Desktop",
            "Asura.Desktop.csproj"));
        var links = project.Descendants("Content")
            .Select(element => (string?)element.Attribute("Link"))
            .Where(static link => link is not null)
            .ToArray();

        Assert.Contains(
            "runtimes/$(AsuraEffectiveRuntimeIdentifier)/native/$(AsuraWorkspaceGatewayHostName)",
            links,
            StringComparer.Ordinal);
        Assert.Contains("runtimes/osx-arm64/workspace-runtime/%(RecursiveDir)%(Filename)%(Extension)", links, StringComparer.Ordinal);
        Assert.DoesNotContain(links, link => link!.StartsWith("runtimes/linux-arm64/guest/", StringComparison.Ordinal));
        Assert.DoesNotContain("AsuraWorkspaceGatewayGuest", project.ToString(), StringComparison.Ordinal);

        var validation = project.Descendants("Target").Single(target => string.Equals(
            (string?)target.Attribute("Name"),
            "ValidateWorkspaceGatewayPayload",
            StringComparison.Ordinal));
        Assert.Contains(
            validation.Descendants("Error"),
            error => ((string?)error.Attribute("Text"))?.Contains(
                "build-workspace-network-gateway.sh --rid osx-arm64",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DevelopmentAndReleaseAssemblyBuildAndVerifyTheGatewayClosure()
    {
        var bootstrap = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "bootstrap.sh"));
        var rehearsal = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "rehearse-macos-release.sh"));
        var package = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "package-macos.sh"));

        Assert.Contains("build-workspace-network-gateway.sh\" --rid osx-arm64", bootstrap, StringComparison.Ordinal);
        Assert.Contains("./scripts/build-workspace-network-gateway.sh --rid osx-arm64", rehearsal, StringComparison.Ordinal);
        Assert.Contains("-p:AsuraWorkspaceGatewayRequired=true", package, StringComparison.Ordinal);
        Assert.Contains("runtimes/osx-arm64/native/${workspace_gateway_host_name}", package, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimes/linux-arm64/guest", package, StringComparison.Ordinal);
        Assert.DoesNotContain("guest_payload_relative", File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "run-macos-development.sh")), StringComparison.Ordinal);
        Assert.Contains("shasum -a 256 -c workspace-network-gateway-MANIFEST.sha256", package, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Asura.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}

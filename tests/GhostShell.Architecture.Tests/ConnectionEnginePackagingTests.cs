using System.Diagnostics;
using System.Xml.Linq;

namespace GhostShell.Architecture.Tests;

public sealed class ConnectionEnginePackagingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void WorkspaceRuntimeBundlesPinnedBootAssetsWithoutTheAppleCli()
    {
        var script = Read("scripts", "build-workspace-runtime.sh");
        Assert.Contains("legal/SOURCE-MANIFEST.sha256", script, StringComparison.Ordinal);
        Assert.Contains("sdk_version=\"0.42.0\"", script, StringComparison.Ordinal);
        Assert.Contains("sdk_revision=\"c0185aea5c04fcd4d1cfe9359e0066a380835403\"", script, StringComparison.Ordinal);
        Assert.Contains("vminit@sha256:cde8a93f9861c664bf2b74b4e2893cf877680806f12e7e989eb9c51b2f2e93bf", script, StringComparison.Ordinal);
        Assert.Contains("8736c054d9223974735394f822000823baef509e1c33405ec798240fa9b6e4b5", script, StringComparison.Ordinal);
        Assert.Contains("prepare-initfs", script, StringComparison.Ordinal);
        Assert.Contains("--disable-automatic-resolution", script, StringComparison.Ordinal);
        Assert.Contains("kernel.config", script, StringComparison.Ordinal);
        Assert.Contains("legal/sources/", script, StringComparison.Ordinal);
        Assert.Contains("swift-stdlib-tool --copy", script, StringComparison.Ordinal);
        Assert.Contains("install_name_tool -delete_rpath", script, StringComparison.Ordinal);
        Assert.Contains("Missing bundled Swift library", script, StringComparison.Ordinal);
        Assert.DoesNotContain("container system", script, StringComparison.Ordinal);
        Assert.DoesNotContain("container image", script, StringComparison.Ordinal);
        Assert.Contains("build-workspace-runtime.sh\" --test", Read("scripts", "check-network-native.sh"), StringComparison.Ordinal);
    }

    [Fact]
    public void OpenVpnBuildIntermediatesStayOutsideTheShippingRidDirectory()
    {
        var build = Read("scripts", "build-openvpn-engine.sh");
        var gate = Read("scripts", "check-network-native.sh");
        Assert.Contains("build_directory=\"${repository_root}/native/artifacts/openvpn-engine-build\"", build, StringComparison.Ordinal);
        Assert.Contains("native/artifacts/openvpn-engine-build/build/CMakeCache.txt", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("osx-arm64/openvpn-engine-build", build, StringComparison.Ordinal);
        Assert.DoesNotContain("osx-arm64/openvpn-engine-build", gate, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceRuntimeKeepsSwiftBuildsOutsideTheSealedSource()
    {
        var script = Read("scripts", "build-workspace-runtime.sh");
        Assert.Contains("swift_scratch_dir=\"${build_dir}/swift\"", script, StringComparison.Ordinal);
        var swiftCommands = script.Split('\n')
            .Where(line => line.Contains("xcrun swift ", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, swiftCommands.Length);
        Assert.All(swiftCommands, line => Assert.Contains("--scratch-path \"${swift_scratch_dir}\"", line, StringComparison.Ordinal));
        Assert.Contains("\"${swift_scratch_dir}/checkouts/\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("${package_dir}/.build", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheWorkspaceRuntimeReceivesVirtualizationPrivileges()
    {
        var entitlements = XDocument.Load(Path.Combine(RepositoryRoot,
            "tools", "GhostShell.Packaging", "MacOS", "WorkspaceRuntime.entitlements"));
        Assert.Equal("com.apple.security.virtualization", Assert.Single(entitlements.Descendants("key")).Value);
        Assert.Single(entitlements.Descendants("true"));
        var signing = Read("scripts", "sign-notarize-macos.sh");
        Assert.Contains("sign_workspace_runtime \"${runtime_file}\"", signing, StringComparison.Ordinal);
        Assert.Contains("runtimes/osx-arm64/workspace-runtime/workspace-runtime", signing, StringComparison.Ordinal);
        var development = Read("scripts", "run-macos-development.sh");
        Assert.Contains("WorkspaceRuntime.entitlements", development, StringComparison.Ordinal);
        Assert.Contains("codesign --verify --strict \"${workspace_runtime}\"", development, StringComparison.Ordinal);
        var package = Read("scripts", "package-macos.sh");
        Assert.Contains("-p:GhostShellWorkspaceRuntimeRequired=true", package, StringComparison.Ordinal);
        Assert.Contains("verify_workspace_runtime_copy", package, StringComparison.Ordinal);
        Assert.Contains("Contents/Resources/workspace-runtime-legal", package, StringComparison.Ordinal);
        Assert.Contains("Contents/Resources/runtimes/osx-arm64/workspace-runtime", package, StringComparison.Ordinal);
        Assert.Contains("${resources_directory}/runtimes/osx-arm64/workspace-runtime", development, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("build-workspace-runtime.sh")]
    [InlineData("package-macos.sh")]
    [InlineData("run-macos-development.sh")]
    [InlineData("sign-notarize-macos.sh")]
    public async Task WorkspacePackagingShellScriptsParse(string script)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/bash")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add(Path.Combine(RepositoryRoot, "scripts", script));
        process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var errors = await process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, errors);
    }

    [Fact]
    public void MandatoryGateChecksNativeFormattingRaceSafetyAndCoreContracts()
    {
        var gate = Read("scripts", "check.sh");
        var native = Read("scripts", "check-network-native.sh");
        Assert.Contains("check-network-native.sh\" \"${mode}\"", gate, StringComparison.Ordinal);
        Assert.Contains("gofmt\" -l", native, StringComparison.Ordinal);
        Assert.Contains("go mod verify", native, StringComparison.Ordinal);
        Assert.Contains("go test -mod=readonly -count=1 ./...", native, StringComparison.Ordinal);
        Assert.Contains("go test -mod=readonly -race -count=1 ./...", native, StringComparison.Ordinal);
        Assert.Contains("go vet -mod=readonly ./...", native, StringComparison.Ordinal);
        Assert.Contains("build-openvpn-engine.sh\" --test", native, StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderLocksSourcesAndProducesTheCompleteMacArm64Closure()
    {
        var script = Read("scripts", "build-macos-connection-engines.sh");

        Assert.Contains("expected_go_version=\"go1.26.3\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("wireproxy", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ocproxy", script, StringComparison.Ordinal);
        Assert.Contains("tailscale_version=\"v1.98.2\"", script, StringComparison.Ordinal);
        Assert.Contains("openconnect_version=\"9.21\"", script, StringComparison.Ordinal);
        Assert.Contains("openssl_version=\"3.6.4\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("libevent", script, StringComparison.Ordinal);
        Assert.Equal(4, script.Split("_sha256=\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("CGO_ENABLED=0", script, StringComparison.Ordinal);
        Assert.Contains("GOOS=darwin", script, StringComparison.Ordinal);
        Assert.Contains("-trimpath", script, StringComparison.Ordinal);
        Assert.Contains("-buildvcs=false", script, StringComparison.Ordinal);
        Assert.Contains("-buildid=", script, StringComparison.Ordinal);
        Assert.Contains("tailscale.com/version.longStamp", script, StringComparison.Ordinal);
        Assert.Contains("ghostshell-openvpn-engine", script, StringComparison.Ordinal);
        Assert.Contains("build-openvpn-engine.sh\" --verify", script, StringComparison.Ordinal);
        Assert.Contains("OPENVPN-VERSIONS.txt", script, StringComparison.Ordinal);
        Assert.Contains("./cmd/tailscale", script, StringComparison.Ordinal);
        Assert.Contains("./cmd/tailscaled", script, StringComparison.Ordinal);
        Assert.Contains("libopenconnect.5.dylib", script, StringComparison.Ordinal);
        Assert.Contains("OPENCONNECT-SOURCE-AND-RELINKING.md", script, StringComparison.Ordinal);
        Assert.Contains("sources/openconnect-${openconnect_version}.tar.gz", script, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.md", script, StringComparison.Ordinal);
        Assert.Contains("MANIFEST.sha256", script, StringComparison.Ordinal);
        Assert.Contains("shasum -a 256 -c", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPublishSeparatesExecutableCodeFromLegalEvidence()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "GhostShell.Desktop.csproj"));
        var links = project.Descendants("Content")
            .Select(element => (string?)element.Attribute("Link"))
            .Where(static link => link is not null)
            .ToArray();

        Assert.Contains(
            "runtimes/osx-arm64/connection-engines/%(Filename)%(Extension)",
            links,
            StringComparer.Ordinal);
        Assert.Contains(
            "connection-engine-legal/%(Filename)%(Extension)",
            links,
            StringComparer.Ordinal);
        Assert.Contains(
            "connection-engine-legal/sources/openconnect-9.21.tar.gz",
            links,
            StringComparer.Ordinal);

        var requiredFiles = project.Descendants("GhostShellConnectionEnginesRequiredFile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static include => include is not null)
            .ToArray();
        Assert.Contains(
            "$(GhostShellConnectionEnginesDirectory)/ghostshell-openvpn-engine",
            requiredFiles,
            StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellConnectionEnginesDirectory)/OPENVPN-VERSIONS.txt",
            requiredFiles,
            StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellConnectionEnginesDirectory)/OPENVPN-MPL-2.0.txt",
            requiredFiles,
            StringComparer.Ordinal);
        Assert.DoesNotContain(requiredFiles,
            file => file!.Contains("wireproxy", StringComparison.Ordinal)
                || file.Contains("ocproxy", StringComparison.Ordinal));
        Assert.Contains(
            "$(GhostShellConnectionEnginesDirectory)/openconnect",
            requiredFiles,
            StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellConnectionEnginesDirectory)/OPENCONNECT-SOURCE-AND-RELINKING.md",
            requiredFiles,
            StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellConnectionEnginesDirectory)/sources/openconnect-9.21.tar.gz",
            requiredFiles,
            StringComparer.Ordinal);

        var validation = project.Descendants("Target").Single(target => string.Equals(
            (string?)target.Attribute("Name"),
            "ValidateConnectionEnginesPayload",
            StringComparison.Ordinal));
        Assert.Contains(
            validation.Descendants("Error"),
            error => ((string?)error.Attribute("Text"))?.Contains(
                "build-macos-connection-engines.sh",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DevelopmentAndReleaseAssemblyBuildAndVerifyTheClosure()
    {
        var bootstrap = Read("scripts", "bootstrap.sh");
        var rehearsal = Read("scripts", "rehearse-macos-release.sh");
        var package = Read("scripts", "package-macos.sh");
        var signing = Read("scripts", "sign-notarize-macos.sh");
        var workflow = Read(".github", "workflows", "repository-gate.yml");

        Assert.Contains("build-macos-connection-engines.sh", bootstrap, StringComparison.Ordinal);
        Assert.Contains("build-openvpn-engine.sh", bootstrap, StringComparison.Ordinal);
        Assert.Contains("./scripts/build-openvpn-engine.sh", rehearsal, StringComparison.Ordinal);
        Assert.Contains("./scripts/build-openvpn-engine.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/build-macos-connection-engines.sh", rehearsal, StringComparison.Ordinal);
        Assert.Contains("./scripts/build-macos-connection-engines.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:GhostShellConnectionEnginesRequired=true", package, StringComparison.Ordinal);
        Assert.Contains("published_connection_engine_code", package, StringComparison.Ordinal);
        Assert.Contains("candidate_connection_engine_code", package, StringComparison.Ordinal);
        Assert.Contains("candidate_connection_engine_legal", package, StringComparison.Ordinal);
        Assert.Contains("shasum -a 256 -c MANIFEST.sha256", package, StringComparison.Ordinal);
        Assert.Contains(
            "find \"${app}/Contents/MacOS\" -type f -print0",
            signing,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("format-and-boundaries", "./scripts/check.sh --quick")]
    [InlineData("macos-early-release", "./scripts/build-workspace-network-gateway.sh")]
    public void NetworkingWorkflowJobsInstallPinnedGoBeforeUsingIt(string jobName, string command)
    {
        var workflow = Read(".github", "workflows", "repository-gate.yml");
        var jobStart = workflow.IndexOf($"\n  {jobName}:", StringComparison.Ordinal);
        Assert.True(jobStart >= 0);
        var nextJob = workflow.IndexOf("\n  ", jobStart + 1, StringComparison.Ordinal);
        while (nextJob >= 0 && workflow[nextJob + 3] == ' ')
        {
            nextJob = workflow.IndexOf("\n  ", nextJob + 1, StringComparison.Ordinal);
        }

        var job = nextJob < 0 ? workflow[jobStart..] : workflow[jobStart..nextJob];
        var setup = job.IndexOf("uses: actions/setup-go@", StringComparison.Ordinal);
        var invocation = job.IndexOf(command, StringComparison.Ordinal);
        Assert.True(setup >= 0 && invocation > setup);
        Assert.Contains("go-version: '1.26.3'", job[setup..invocation], StringComparison.Ordinal);
    }

    [Fact]
    public void BundleBuilderPlacesConnectionEngineLegalEvidenceInResources()
    {
        var source = Read(
            "tools",
            "GhostShell.Packaging",
            "MacOsAppBundleBuilder.cs");

        Assert.Contains(
            "topLevelDirectory is \"connection-engine-legal\" or \"workspace-runtime-legal\" or \"fonts\" or \"ghostty\"",
            source,
            StringComparison.Ordinal);
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryRoot, .. segments]));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GhostShell.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}

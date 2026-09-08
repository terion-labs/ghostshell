using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using GhostShell.Application;
using GhostShell.Testing;

namespace GhostShell.Architecture.Tests;

public sealed partial class RepositoryConventionTests
{
    [Theory]
    [InlineData("api.github.com/repos/terion-labs/ghostshell/releases")]
    [InlineData("releases/latest/download/GhostShell-macOS")]
    [InlineData("Sparkle")]
    [InlineData("appcast")]
    public void Desktop_has_no_automatic_update_client(string updateClientMarker)
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src");
        var productionSource = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".axaml", StringComparison.Ordinal)
                || path.EndsWith(".csproj", StringComparison.Ordinal)
                || path.EndsWith("packages.lock.json", StringComparison.Ordinal))
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));

        Assert.All(productionSource, path => Assert.DoesNotContain(
            updateClientMarker,
            File.ReadAllText(path),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Velopack_dependency_is_isolated_to_the_updates_boundary()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src");
        var projectsReferencingVelopack = Directory
            .EnumerateFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(
                "<PackageReference Include=\"Velopack\"",
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(RepositoryRoot, path))
            .ToArray();

        Assert.Equal(
            ["src/GhostShell.Updates/GhostShell.Updates.csproj"],
            projectsReferencingVelopack,
            StringComparer.Ordinal);
    }

    [Fact]
    public void Github_actions_use_only_reviewed_runners()
    {
        var workflowDirectory = Path.Combine(RepositoryRoot, ".github", "workflows");
        var expectedRunners = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["database-viewer-integration.yml"] = ["runs-on: macos-15"],
            ["macos-product-identity.yml"] = ["runs-on: macos-26"],
            ["repository-gate.yml"] = ["runs-on: macos-15", "runs-on: macos-26"],
            ["website.yml"] = ["runs-on: ubuntu-latest"],
        };
        var workflows = Directory
            .GetFiles(workflowDirectory, "*.yml")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expectedRunners.Keys.Order(StringComparer.Ordinal),
            workflows.Select(Path.GetFileName),
            StringComparer.Ordinal);
        foreach (var workflow in workflows)
        {
            var runnerDeclarations = File.ReadLines(workflow)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("runs-on:", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(runnerDeclarations);
            Assert.All(
                runnerDeclarations,
                declaration => Assert.Contains(
                    declaration,
                    expectedRunners[Path.GetFileName(workflow)],
                    StringComparer.Ordinal));
        }

        Assert.Contains(
            "runs-on: macos-26",
            File.ReadLines(Path.Combine(workflowDirectory, "repository-gate.yml"))
                .Select(line => line.Trim()),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Connection_editor_keeps_test_progress_in_its_fixed_footer()
    {
        var dialog = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "ConnectionEditorDialog.axaml"));
        var root = Assert.IsType<XElement>(dialog.Root);
        var shell = Assert.Single(
            root.Descendants(),
            element => string.Equals(element.Name.LocalName, "DialogShell", StringComparison.Ordinal));
        var test = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Click"), "OnTestClick", StringComparison.Ordinal));

        Assert.Equal("{Binding TestFooterHint}", AttributeValue(shell, "FooterHint"));
        Assert.Equal("{Binding !IsTesting}", AttributeValue(test, "IsEnabled"));
        Assert.Equal("{Binding TestLabel}", AttributeValue(test, "Content"));
    }

    [Fact]
    public void Database_password_prompt_offers_explicit_opt_in_credential_storage()
    {
        var dialog = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "DatabasePasswordPromptDialog.axaml"));
        var root = Assert.IsType<XElement>(dialog.Root);
        var savePassword = Assert.Single(
            root.Descendants(),
            element => string.Equals(AttributeValue(element, "Name"), "SavePasswordCheckBox", StringComparison.Ordinal));

        Assert.Equal("CheckBox", savePassword.Name.LocalName);
        Assert.Equal("False", AttributeValue(savePassword, "IsChecked"));
        Assert.Equal("False", AttributeValue(savePassword, "IsVisible"));
        Assert.Equal(
            "Save password in system credential store",
            AttributeValue(savePassword, "AutomationProperties.Name"));
    }

    [Fact]
    public void Macos_packaging_opts_the_apphost_into_current_native_chrome()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        var declarationScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "declare-macos-sdk26.sh"));
        var desktopProject = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));

        Assert.Contains(
            "-set-build-version macos \"${minimum_macos}\" 26.0",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "signing_identifier=\"app.ghostshell\"",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            declarationScript.Split(
                "--identifier \"${signing_identifier}\"",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "declared_sdk",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not declare macOS SDK 26.0",
            declarationScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "declare-macos-sdk26.sh",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "AfterTargets=\"Build\"",
            desktopProject,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Macos_release_packages_a_speed_optimized_native_aot_executable()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        var desktopProject = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));
        var workflow = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                ".github",
                "workflows",
                "repository-gate.yml"));
        var infoPlist = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "tools",
                "GhostShell.Packaging",
                "MacOS",
                "Info.plist.template"));

        Assert.Contains("<PublishAot>true</PublishAot>", desktopProject, StringComparison.Ordinal);
        Assert.Contains(
            "<OptimizationPreference>Speed</OptimizationPreference>",
            desktopProject,
            StringComparison.Ordinal);
        Assert.Contains("<StripSymbols>true</StripSymbols>", desktopProject, StringComparison.Ordinal);
        Assert.Contains("-p:GhostShellMacReleaseNativeAot=true", packageScript, StringComparison.Ordinal);
        Assert.Contains("packages.${runtime_identifier}.aot.lock.json", packageScript, StringComparison.Ordinal);
        Assert.Contains("--managed-evidence", packageScript, StringComparison.Ordinal);
        Assert.Contains("*.runtimeconfig.json", packageScript, StringComparison.Ordinal);
        Assert.Contains("brew install lld@22", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("<string>GhostShell.icns</string>", infoPlist, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            RepositoryRoot,
            "assets",
            "macos",
            "GhostShell.icns")));
        Assert.True(File.Exists(Path.Combine(
            RepositoryRoot,
            "assets",
            "macos",
            "GhostShell.icon",
            "icon.json")));
    }

    [Fact]
    public void Macos_product_identity_is_reviewed_hashed_and_compiled_by_xcode()
    {
        var manifestPath = Path.Combine(
            RepositoryRoot,
            "assets",
            "macos",
            "product-identity.json");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = manifest.RootElement;

        Assert.Equal(ProductIdentity.DisplayName, root.GetProperty("displayName").GetString());
        Assert.Equal(ProductIdentity.ExecutableName, root.GetProperty("executableName").GetString());
        Assert.Equal(ProductIdentity.BundleIdentifier, root.GetProperty("bundleIdentifier").GetString());
        Assert.Equal("approved", root.GetProperty("approval").GetProperty("status").GetString());
        Assert.Equal("MIT", root.GetProperty("artwork").GetProperty("license").GetString());
        Assert.Contains(
            "transform=\"translate(143.5 185) scale(1.416)\"",
            File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "assets",
                "macos",
                "GhostShell.icon",
                "Assets",
                "logo.svg")),
            StringComparison.Ordinal);
        Assert.Equal(
            ["Default", "Dark", "TintedLight", "TintedDark", "ClearLight", "ClearDark"],
            root.GetProperty("requiredAppearances")
                .EnumerateArray()
                .Select(value => value.GetString()),
            StringComparer.Ordinal);

        var files = root.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(3, files.Length);
        foreach (var file in files)
        {
            var relativePath = file.GetProperty("path").GetString()!;
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(
                    RepositoryRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)))));
            Assert.Equal(file.GetProperty("sha256").GetString(), actualHash);
        }

        var infoPlist = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "GhostShell.Packaging",
            "MacOS",
            "Info.plist.template"));
        Assert.Contains("<key>CFBundleDisplayName</key>\n    <string>GhostSHELL</string>", infoPlist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleExecutable</key>\n    <string>GhostShell</string>", infoPlist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleIdentifier</key>\n    <string>app.ghostshell</string>", infoPlist, StringComparison.Ordinal);
        Assert.Contains("<key>CFBundleIconName</key>\n    <string>GhostShell</string>", infoPlist, StringComparison.Ordinal);

        var iconCompiler = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "compile-macos-app-icon.sh"));
        var packageScript = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "package-macos.sh"));
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "repository-gate.yml"));
        var identityWorkflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "macos-product-identity.yml"));
        Assert.Contains("--app-icon GhostShell", iconCompiler, StringComparison.Ordinal);
        Assert.Contains("--minimum-deployment-target \"${minimum_macos}\"", iconCompiler, StringComparison.Ordinal);
        Assert.Contains("requires Xcode actool 26 or newer", iconCompiler, StringComparison.Ordinal);
        Assert.Contains("--asset-catalog \"${compiled_icon_directory}/Assets.car\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("assetutil --info \"${candidate_asset_catalog}\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("Select full Xcode 26 for adaptive icon compilation", workflow, StringComparison.Ordinal);
        Assert.Contains("Contents/Resources/Assets.car", workflow, StringComparison.Ordinal);
        Assert.Contains("branches: [main]", identityWorkflow, StringComparison.Ordinal);
        Assert.Contains("./scripts/compile-macos-app-icon.sh", identityWorkflow, StringComparison.Ordinal);
        Assert.Contains("macos-product-identity", identityWorkflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Managed_third_party_notice_table_matches_the_macos_catalog()
    {
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            RepositoryRoot,
            "licenses",
            "managed-components.json")));
        var expectedRows = catalog.RootElement
            .GetProperty("dependencies")
            .EnumerateArray()
            .Where(component =>
                string.Equals(
                    component.GetProperty("kind").GetString(),
                    "nuget",
                    StringComparison.Ordinal)
                || component.GetProperty("identity").GetString()!.StartsWith(
                    "Exclr8Cef",
                    StringComparison.Ordinal)
                || component.TryGetProperty("vendorSource", out _))
            .Select(component =>
            {
                var identity = component.GetProperty("identity").GetString()!;
                if (component.TryGetProperty("vendorSource", out var vendorSource))
                {
                    var upstream = vendorSource.GetProperty("upstreamIdentity").GetString()!;
                    var notice = identity switch
                    {
                        "Microsoft.Data.SqlClient.Routed/6.0.2" => "sqlclient-MIT.txt",
                        "SSH.NET/2026.0.0" => "sshnet-MIT.txt",
                        "SharpCompress/0.50.3" => "sharpcompress-MIT.txt",
                        _ => throw new InvalidOperationException("Unreviewed source-built notice identity."),
                    };
                    var upstreamSeparator = upstream.LastIndexOf('/');
                    var localIdentity = string.Equals(identity, upstream, StringComparison.Ordinal)
                        ? string.Empty
                        : $" local project identity `{identity}`;";
                    return $"| `{upstream[..upstreamSeparator]}` | `{upstream[(upstreamSeparator + 1)..]}` | "
                        + $"MIT, patched source build;{localIdentity} `{notice}` |";
                }
                var separator = identity.LastIndexOf('/');
                var name = identity[..separator];
                var version = identity[(separator + 1)..];
                var kind = component.GetProperty("kind").GetString();
                var license = string.Equals(kind, "project", StringComparison.Ordinal)
                    ? "MIT (vendored project; see `Exclr8CEF-MIT.txt`)"
                    : component.GetProperty("nuspecLicenseType").GetString() == "file"
                        ? $"NOASSERTION (nuspec file: `{component.GetProperty("nuspecLicense").GetString()}`)"
                        : component.GetProperty("licenseDeclared").GetString();
                return $"| `{name}` | `{version}` | {license} |";
            })
            .ToArray();
        var noticeLines = File.ReadAllLines(Path.Combine(
            RepositoryRoot,
            "licenses",
            "THIRD-PARTY-NOTICES.md"));
        var tableStart = Array.IndexOf(noticeLines, "| Package | Version | License |");
        var tableEnd = Array.IndexOf(noticeLines, "## Lucide icon geometry");

        Assert.True(tableStart >= 0 && tableEnd > tableStart);
        Assert.Equal(
            expectedRows,
            noticeLines[(tableStart + 2)..tableEnd],
            StringComparer.Ordinal);
        Assert.Equal(132, expectedRows.Length);
        Assert.Equal(127, catalog.RootElement.GetProperty("dependencies").EnumerateArray()
            .Count(component => string.Equals(component.GetProperty("kind").GetString(), "nuget", StringComparison.Ordinal)));
        Assert.Equal(3, catalog.RootElement.GetProperty("dependencies").EnumerateArray()
            .Count(component => component.TryGetProperty("vendorSource", out _)));
        Assert.Contains(expectedRows, row => row.Contains("DuckDB.NET.Bindings.Full` | `1.5.5` | MIT", StringComparison.Ordinal));
        Assert.DoesNotContain(expectedRows, row => row.Contains("DuckDB.NET.Bindings.Full` | `1.2.1`", StringComparison.Ordinal));
    }

    [Fact]
    public void Tag_release_publishes_stable_latest_download_assets()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "repository-gate.yml"));
        var releaseScript = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "package-macos-github-release.sh"));

        Assert.Contains("Verify macOS legal publication clearance", workflow, StringComparison.Ordinal);
        Assert.Contains("macos-release-legal", workflow, StringComparison.Ordinal);
        Assert.Contains("--require-clearance", workflow, StringComparison.Ordinal);

        Assert.Contains("archive=\"GhostShell-macOS-arm64.zip\"", workflow, StringComparison.Ordinal);
        Assert.Contains("distribution/GhostShell-macOS-arm64.zip.sha256", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("GhostShell-macOS-arm64-${RELEASE_VERSION}.zip", workflow, StringComparison.Ordinal);
        Assert.Contains("package-macos-github-release.sh", workflow, StringComparison.Ordinal);

        Assert.Contains("\"${dotnet}\" tool restore", releaseScript, StringComparison.Ordinal);
        Assert.Contains("releases.osx-arm64-stable.json", workflow, StringComparison.Ordinal);
        Assert.Contains("app.ghostshell-${RELEASE_VERSION}-osx-arm64-stable-full.nupkg", workflow, StringComparison.Ordinal);
        Assert.Contains("--signDisableDeep true", releaseScript, StringComparison.Ordinal);
        Assert.Contains("--noInst true", releaseScript, StringComparison.Ordinal);
        Assert.Contains("velopack-macos-validate", releaseScript, StringComparison.Ordinal);
        Assert.Contains("record-macos-signing-evidence.sh", releaseScript, StringComparison.Ordinal);
        Assert.Contains("name: ghostshell-macos-arm64", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n      contents: write", workflow, StringComparison.Ordinal);
        Assert.Contains("if: startsWith(github.ref, 'refs/tags/v')", workflow, StringComparison.Ordinal);
        Assert.Contains("GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release create \"${GITHUB_REF_NAME}\"", workflow, StringComparison.Ordinal);
        Assert.Contains("--latest", workflow, StringComparison.Ordinal);
        Assert.Contains("--verify-tag", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--prerelease", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Macos_early_release_allows_native_payload_builds_to_finish()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "repository-gate.yml"));
        var releaseJob = workflow[workflow.IndexOf(
            "  macos-early-release:",
            StringComparison.Ordinal)..];

        Assert.Contains("    timeout-minutes: 180", releaseJob, StringComparison.Ordinal);
    }

    [Fact]
    public void Macos_packaging_keeps_vendored_project_versions_independent()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        var buildProperties = File.ReadAllText(
            Path.Combine(RepositoryRoot, "Directory.Build.props"));

        Assert.Contains(
            "-p:GhostShellProductVersion=\"${version}\"",
            packageScript,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            packageScript.Split(
                "-p:GhostShellProductVersion=\"${version}\"",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "nuget_packages=\"${NUGET_PACKAGES:-${repository_dir}/.nuget/packages}\"",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:GhostShellCefRuntimeArtifactDirectory=\"${cef_runtime_root}\"",
            packageScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain("-p:Version=", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:AssemblyVersion=", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:FileVersion=", packageScript, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:InformationalVersion=", packageScript, StringComparison.Ordinal);
        Assert.Contains(
            "StartsWith('GhostShell')",
            buildProperties,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Version>$(GhostShellProductVersion)</Version>",
            buildProperties,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Full_macos_packaging_stays_arm64_until_x64_evidence_exists()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        var packagingGuide = File.ReadAllText(
            Path.Combine(RepositoryRoot, "docs", "macos-packaging.md"));

        Assert.Contains(
            "[--runtime-identifier osx-arm64]",
            packageScript,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "osx-arm64|osx-x64",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "currently supports only osx-arm64",
            packageScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "Full Intel application packaging fails fast",
            packagingGuide,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Macos_packaging_path_leak_checks_cover_merged_first_party_assemblies()
    {
        var packageScript = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));

        Assert.Contains("\"GhostShell.Databases.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Docker.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Docking.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Git.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Previews.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Redis.dll\"", packageScript, StringComparison.Ordinal);
        Assert.Contains("\"GhostShell.Updates.dll\"", packageScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Macos_dotnet_run_assembles_the_required_cef_application_bundle()
    {
        var desktopProject = File.ReadAllText(
            Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                "GhostShell.Desktop.csproj"));
        var developmentRunner = File.ReadAllText(
            Path.Combine(RepositoryRoot, "scripts", "run-macos-development.sh"));

        Assert.Contains("ConfigureMacOsDevelopmentRun", desktopProject, StringComparison.Ordinal);
        Assert.Contains(
            "BeforeTargets=\"ComputeRunArguments\"",
            desktopProject,
            StringComparison.Ordinal);
        Assert.Contains("scripts/run-macos-development.sh", desktopProject, StringComparison.Ordinal);
        Assert.Contains("Chromium Embedded Framework.framework", developmentRunner, StringComparison.Ordinal);
        Assert.Contains("GhostSHELL Helper (Renderer)", developmentRunner, StringComparison.Ordinal);
        Assert.Contains("Contents/MacOS/GhostShell", developmentRunner, StringComparison.Ordinal);
    }

    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly ApplicationViewCatalog ApplicationViews =
        ApplicationViewCatalog.Load();

    [Fact]
    public void ProductSourceUsesPanelTerminology()
    {
        var productFiles = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
            .Append(Path.Combine(RepositoryRoot, "README.md"));

        foreach (var file in productFiles)
        {
            var source = File.ReadAllText(file);
            Assert.False(
                PaneTerminology().IsMatch(source),
                $"{Path.GetRelativePath(RepositoryRoot, file)} uses tmux's pane terminology; GhostSHELL calls this a panel.");
        }
    }

    [Fact]
    public void ArchitectureDecisionNumbersAreUniqueAndMatchTheirHeadings()
    {
        var decisions = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "docs", "adr"), "*.md")
            .Select(path => new
            {
                Path = path,
                Number = Path.GetFileName(path).Split('-', 2)[0],
                Heading = File.ReadLines(path).FirstOrDefault() ?? string.Empty,
            })
            .ToArray();

        Assert.Equal(
            decisions.Length,
            decisions.Select(decision => decision.Number).Distinct(StringComparer.Ordinal).Count());
        Assert.All(decisions, decision => Assert.StartsWith(
            $"# ADR {decision.Number}:",
            decision.Heading,
            StringComparison.Ordinal));
    }

    [Fact]
    public void VisibleApplicationTextUsesScalableFontResources()
    {
        var applicationRoot = Path.Combine(RepositoryRoot, "src", "GhostShell.App");
        var xamlFiles = Directory
            .EnumerateFiles(applicationRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));

        foreach (var file in xamlFiles)
        {
            var source = File.ReadAllText(file);
            var literalElements = LiteralElementFontSize()
                .Matches(source)
                .Select(match => match.Groups["element"].Value)
                .Where(element => !string.Equals(
                    element,
                    "icons:SymbolIcon",
                    StringComparison.Ordinal))
                .ToArray();

            Assert.True(
                literalElements.Length == 0,
                $"{Path.GetRelativePath(RepositoryRoot, file)} contains literal font sizes on: " +
                string.Join(", ", literalElements));
            Assert.False(
                LiteralFontSizeSetter().IsMatch(source),
                $"{Path.GetRelativePath(RepositoryRoot, file)} contains a literal font-size style; use a ShellFontSize dynamic resource.");
        }
    }

    [Fact]
    public void IconOnlyButtonsHaveExplicitAccessibleNames()
    {
        foreach (var file in ApplicationXamlFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            var unnamedButtons = document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "Button", StringComparison.Ordinal))
                .Where(ContainsIconWithoutVisibleText)
                .Where(element => !HasAttribute(element, "AutomationProperties.Name"))
                .Select(DescribeElement)
                .ToArray();

            Assert.True(
                unnamedButtons.Length == 0,
                $"{Path.GetRelativePath(RepositoryRoot, file)} contains icon-only buttons without explicit accessible names: "
                + string.Join(", ", unnamedButtons));
        }
    }

    [Fact]
    public void InWindowOverlaysTrapKeyboardFocus()
    {
        foreach (var file in ApplicationXamlFiles())
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            var unboundedOverlays = document
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "SurfaceCard"
, StringComparison.Ordinal) && string.Equals(
                        AttributeValue(element, "Elevation"),
                        "Overlay",
                        StringComparison.Ordinal))
                .Where(element => !string.Equals(
                    AttributeValue(element, "KeyboardNavigation.TabNavigation"),
                    "Cycle",
                    StringComparison.Ordinal))
                .Select(DescribeElement)
                .ToArray();

            Assert.True(
                unboundedOverlays.Length == 0,
                $"{Path.GetRelativePath(RepositoryRoot, file)} contains overlays that do not cycle keyboard focus: "
                + string.Join(", ", unboundedOverlays));
        }
    }

    [Fact]
    public void AgentAuthorizationSurfacesReflowAtLargeTextScales()
    {
        var views = Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views");
        var mainWindow = ApplicationViews.FindUniqueOwnerDocument(
            "the agent authorization panel",
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "AgentPanel"))
            .Document;
        var agentPanel = Assert.Single(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "AgentPanel"));
        var agentLayout = Assert.Single(
            agentPanel.Elements(),
            element => string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal));

        Assert.Equal(
            "Auto,*,Auto,Auto",
            AttributeValue(agentLayout, "RowDefinitions"));
        Assert.All(
            agentLayout.Elements()
                .Where(element => string.Equals(element.Name.LocalName, "Grid", StringComparison.Ordinal))
                .Take(2),
            row => Assert.False(
                string.IsNullOrWhiteSpace(AttributeValue(row, "MinHeight")),
                "Text-bearing agent header rows require minima, not fixed heights."));
        var activityScroller = Assert.Single(
            agentLayout.Elements(),
            element => string.Equals(element.Name.LocalName, "ScrollViewer"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Name"),
                    "AgentChatTranscript",
                    StringComparison.Ordinal));
        Assert.Equal("1", AttributeValue(activityScroller, "Grid.Row"));
        var actionCancel = Assert.Single(
            activityScroller.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnCancelAgentActionClick",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding AgentChat.CanCancelActiveAction}",
            AttributeValue(actionCancel, "IsEnabled"));
        Assert.Contains(
            "ActiveActionCancellationLabel",
            AttributeValue(actionCancel, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(
                    actionCancel,
                    "AutomationProperties.HelpText")));
        Assert.Contains(
            agentLayout.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnCancelAgentChatClick",
                    StringComparison.Ordinal)
                && string.Equals(
                    AttributeValue(element, "IsEnabled"),
                    "{Binding AgentChat.CanRequestStop}",
                    StringComparison.Ordinal));
        Assert.Equal(
            "AI agent activity",
            AttributeValue(activityScroller, "AutomationProperties.Name"));
        Assert.Contains(
            mainWindow.Descendants(),
            element => string.Equals(element.Name.LocalName, "StatusChip"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name")
, "AI agent state", StringComparison.Ordinal));
        Assert.Contains(
            agentLayout.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Text"), "{Binding AgentChat.Status}"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "AutomationProperties.Name")
, "AI agent status", StringComparison.Ordinal));
        Assert.DoesNotContain(
            agentLayout.Descendants(),
            element => (AttributeValue(element, "Text") ?? string.Empty)
                .Contains("Expires ·", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentPanelDoesNotExposeLegacyTargetSelectors()
    {
        var mainWindow = ApplicationViews.FindUniqueOwnerDocument(
            "the AI agent panel",
            element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "AgentPanel"))
            .Document;
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding AgentRunScopeOptions}",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            mainWindow.Descendants(),
            element => string.Equals(
                    AttributeValue(element, "ItemsSource"),
                    "{Binding AgentTerminalSelectionOptions}",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void SavedScreenDeleteUndoIsVisibleAndKeyboardReachable()
    {
        var notice = ApplicationViews
            .FindUniqueNamedElement("SavedScreenDeleteUndoNotice")
            .Element;

        Assert.Equal("SurfaceCard", notice.Name.LocalName);
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.HasPending}",
            AttributeValue(notice, "IsVisible"));
        Assert.Equal("Polite", AttributeValue(notice, "AutomationProperties.LiveSetting"));
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.Status}",
            AttributeValue(notice, "AutomationProperties.Name"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(notice, "AutomationProperties.HelpText")));

        var undo = Assert.Single(
            notice.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Name"),
                    "UndoDeletedSavedScreenButton",
                    StringComparison.Ordinal));
        Assert.Equal(
            "OnUndoDeletedSavedScreenClick",
            AttributeValue(undo, "Click"));
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.CanUndo}",
            AttributeValue(undo, "IsEnabled"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(undo, "AutomationProperties.Name")));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(undo, "AutomationProperties.HelpText")));

        var dismiss = Assert.Single(
            notice.Descendants(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnDismissSavedScreenDeleteUndoClick",
                    StringComparison.Ordinal));
        Assert.Equal(
            "{Binding SavedScreenDeleteUndo.CanUndo}",
            AttributeValue(dismiss, "IsEnabled"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(dismiss, "AutomationProperties.Name")));
    }

    [Fact]
    public void RuntimeTabIconIsOpticallyAlignedWithTitle()
    {
        var tabStrip = ApplicationViews.FindUniqueNamedElement("TabScrollViewer").Element;
        var title = Assert.Single(
            tabStrip.Descendants(),
            element => string.Equals(element.Name.LocalName, "TextBlock"
, StringComparison.Ordinal) && HasClass(element, "RuntimeTabTitle"));
        var icon = Assert.Single(
            tabStrip.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(AttributeValue(element, "Symbol")
, "{Binding IconSymbol}", StringComparison.Ordinal));
        var opticalOffset = Assert.Single(
            icon.Descendants(),
            element => string.Equals(element.Name.LocalName, "TranslateTransform"
, StringComparison.Ordinal));

        Assert.Equal("Center", AttributeValue(title, "VerticalAlignment"));
        Assert.Equal("Center", AttributeValue(icon, "VerticalAlignment"));
        Assert.Equal("-1", AttributeValue(opticalOffset, "Y"));
    }

    [Fact]
    public void RuntimeTabReorderingHasPointerFeedbackAndKeyboardParity()
    {
        // The strip is one reusable control hosted at whichever edge the profile
        // selects, so the tab template lives in that component rather than being
        // copied per edge.
        var ownedTabStrip = ApplicationViews.FindUniqueNamedElement(
            "TabScrollViewer");
        var tabStrip = ownedTabStrip.Element;

        // The reorder live region belongs to the shell route that hosts the strip.
        var workspace = ApplicationViews
            .FindUniqueNamedElement("RuntimeTabStripSide")
            .Owner
            .Document;

        Assert.Equal("ScrollViewer", tabStrip.Name.LocalName);
        // Scroll bars follow the strip's orientation. Assigned from code, not
        // bound: the consumer sets Orientation after the strip's own bindings
        // read their value, and the stale Hidden axis let a side-docked strip
        // grow past its viewport and clip its close buttons.
        var stripCode = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "Components",
            "RuntimeTabStripView.axaml.cs"));
        Assert.Contains("private void SyncScrollBars()", stripCode, StringComparison.Ordinal);
        Assert.Contains(
            "TabScrollViewer.HorizontalScrollBarVisibility",
            stripCode,
            StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(tabStrip, "AutomationProperties.Name")));

        var dropTarget = Assert.Single(
            tabStrip.Descendants(),
            element => string.Equals(element.Name.LocalName, "Grid"
, StringComparison.Ordinal) && HasClass(element, "RuntimeTabDropTarget"));
        Assert.Equal("True", AttributeValue(dropTarget, "DragDrop.AllowDrop"));
        Assert.Equal(
            "OnDragEnter",
            AttributeValue(dropTarget, "DragDrop.DragEnter"));
        Assert.Equal(
            "OnDragLeave",
            AttributeValue(dropTarget, "DragDrop.DragLeave"));
        Assert.Equal(
            "OnDragOver",
            AttributeValue(dropTarget, "DragDrop.DragOver"));
        Assert.Equal(
            "OnDrop",
            AttributeValue(dropTarget, "DragDrop.Drop"));

        var indicators = dropTarget
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "Border"
, StringComparison.Ordinal) && HasClass(element, "RuntimeTabDropIndicator"))
            .ToArray();
        Assert.Equal(2, indicators.Length);
        Assert.Contains(indicators, indicator => HasClass(indicator, "Before"));
        Assert.Contains(indicators, indicator => HasClass(indicator, "After"));
        Assert.All(indicators, indicator =>
        {
            Assert.Equal("False", AttributeValue(indicator, "IsVisible"));
            Assert.Equal("False", AttributeValue(indicator, "IsHitTestVisible"));
            Assert.Contains(
                "ShellAccentBrush",
                AttributeValue(indicator, "Background"),
                StringComparison.Ordinal);
        });

        var activator = Assert.Single(
            dropTarget.Elements(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && HasClass(element, "RuntimeTabActivator"));
        Assert.Null(AttributeValue(activator, "PointerMoved"));
        Assert.Null(AttributeValue(activator, "PointerReleased"));
        Assert.Null(AttributeValue(activator, "PointerCaptureLost"));
        Assert.Null(AttributeValue(activator, "PointerPressed"));
        Assert.Contains("OnTabPointerPressed", stripCode, StringComparison.Ordinal);
        Assert.Contains("OnTabPointerMoved", stripCode, StringComparison.Ordinal);
        Assert.Contains("OnTabPointerReleased", stripCode, StringComparison.Ordinal);
        Assert.Contains("OnTabPointerCaptureLost", stripCode, StringComparison.Ordinal);
        Assert.Contains("RoutingStrategies.Tunnel", stripCode, StringComparison.Ordinal);
        Assert.Contains("FindTabActivator(e.Source)", stripCode, StringComparison.Ordinal);
        Assert.DoesNotContain(
            dropTarget.Descendants(),
            element => string.Equals(element.Name.LocalName, "SymbolIcon"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Symbol"),
                    "ReOrderDotsVertical",
                    StringComparison.Ordinal));

        var mainWindowCodeBehind = ApplicationViews.FindPartialClassSources("MainWindow");
        var pointerPressedStart = mainWindowCodeBehind.IndexOf(
            "private void OnRuntimeTabDragPointerPressed(",
            StringComparison.Ordinal);
        var pointerMovedStart = mainWindowCodeBehind.IndexOf(
            "private void OnRuntimeTabDragPointerMoved(",
            StringComparison.Ordinal);
        Assert.True(pointerPressedStart >= 0);
        Assert.True(pointerMovedStart > pointerPressedStart);
        Assert.DoesNotContain(
            "e.Handled = true;",
            mainWindowCodeBehind[pointerPressedStart..pointerMovedStart],
            StringComparison.Ordinal);

        Assert.Contains(
            "Activate tab",
            AttributeValue(activator, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Drag the tab to reorder it",
            AttributeValue(activator, "AutomationProperties.HelpText"),
            StringComparison.Ordinal);
        var close = Assert.Single(
            dropTarget.Elements(),
            element => string.Equals(element.Name.LocalName, "Button"
, StringComparison.Ordinal) && string.Equals(
                    AttributeValue(element, "Click"),
                    "OnClose",
                    StringComparison.Ordinal));
        Assert.Contains(
            "Title",
            AttributeValue(close, "AutomationProperties.Name"),
            StringComparison.Ordinal);
        Assert.False(
            string.IsNullOrWhiteSpace(
                AttributeValue(close, "AutomationProperties.HelpText")));

        var status = Assert.Single(
            workspace.Descendants(),
            element => element.Name == XName.Get("LiveRegionTextBlock", "using:GhostShell.App.Controls") && string.Equals(
                    AttributeValue(element, "Text"),
                    "{Binding TabReorderStatus}",
                    StringComparison.Ordinal));
        Assert.Equal("Polite", AttributeValue(status, "AutomationProperties.LiveSetting"));
        Assert.Contains(
            "TabReorderStatus",
            AttributeValue(status, "AutomationProperties.Name"),
            StringComparison.Ordinal);

        var dragController = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.App",
            "Views",
            "RuntimeTabDragController.cs"));
        Assert.Contains(
            "DataFormat.CreateInProcessFormat<RuntimeTabDragPayload>",
            dragController,
            StringComparison.Ordinal);
        Assert.Contains(
            "new RuntimeTabActiveDrag(",
            dragController,
            StringComparison.Ordinal);
        Assert.Contains(
            "presentation.ShowGhost(",
            dragController,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveDrop(",
            dragController,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DragDrop.DoDragDropAsync(",
            dragController,
            StringComparison.Ordinal);
        Assert.Contains(
            "DragThreshold",
            dragController,
            StringComparison.Ordinal);
        Assert.Contains(
            "viewModel.MoveTabAsync(",
            dragController,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DataObject", dragController, StringComparison.Ordinal);
    }

    private static IEnumerable<string> ApplicationXamlFiles()
    {
        var applicationRoot = Path.Combine(RepositoryRoot, "src", "GhostShell.App");
        return Directory
            .EnumerateFiles(applicationRoot, "*.axaml", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));
    }

    private static bool ContainsIconWithoutVisibleText(XElement button)
    {
        var containsIcon = button
            .Descendants()
            .Any(element => string.Equals(element.Name.LocalName, "SymbolIcon", StringComparison.Ordinal));
        var hasContent = button.Attributes().Any(attribute => string.Equals(attribute.Name.LocalName, "Content"
, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attribute.Value));
        var containsText = button
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "TextBlock", StringComparison.Ordinal))
            .Any(element => element.Attributes().Any(attribute => string.Equals(attribute.Name.LocalName, "Text"
, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(attribute.Value)));
        return containsIcon && !hasContent && !containsText;
    }

    private static bool HasClass(XElement element, string className) =>
        (AttributeValue(element, "Classes") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.Ordinal);

    private static bool HasAttribute(XElement element, string name) =>
        AttributeValue(element, name) is not null;

    private static string? AttributeValue(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal))
            ?.Value;

    private static string DescribeElement(XElement element)
    {
        var line = (element as IXmlLineInfo)?.LineNumber ?? 0;
        var name = AttributeValue(element, "Name") ?? element.Name.LocalName;
        return line > 0 ? $"{name} at line {line}" : name;
    }

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar).Contains(segment, StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GhostShell.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate the GhostSHELL repository root.");
    }

    [GeneratedRegex(
        @"\bpanes?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex PaneTerminology();

    [GeneratedRegex(
        "<(?<element>[A-Za-z0-9:]+)\\b[^>]*\\bFontSize=\"[0-9]",
        RegexOptions.CultureInvariant | RegexOptions.Singleline,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex LiteralElementFontSize();

    [GeneratedRegex(
        "<Setter\\s+Property=\"FontSize\"\\s+Value=\"[0-9]",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1_000)]
    private static partial Regex LiteralFontSizeSetter();
}

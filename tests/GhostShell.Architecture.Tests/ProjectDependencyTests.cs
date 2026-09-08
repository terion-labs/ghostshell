using System.Xml.Linq;

namespace GhostShell.Architecture.Tests;

public sealed class ProjectDependencyTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Theory]
    [InlineData("vendor/exclr8cef/src/Exclr8Cef/Exclr8Cef.csproj")]
    [InlineData("vendor/exclr8cef/src/Exclr8Cef.WebView/Exclr8Cef.WebView.csproj")]
    [InlineData("vendor/sqlclient/upstream/src/Microsoft.Data.SqlClient/netcore/src/Microsoft.Data.SqlClient.Routed.csproj")]
    [InlineData("vendor/sqlclient/tests/Tds.TestSupport.csproj")]
    public void ActiveVendorProjectsHaveNormalSolutionConfigurationMapping(string projectPath)
    {
        // Unmapped references lose the parent's configuration in solution builds.
        var project = Assert.Single(LoadProject("GhostShell.slnx").Descendants("Project"), element =>
            string.Equals((string?)element.Attribute("Path"), projectPath, StringComparison.Ordinal));
        Assert.Empty(project.Elements());
    }

    [Theory]
    [InlineData("vendor/sqlclient/upstream/src/Microsoft.Data.SqlClient/netcore/src/Microsoft.Data.SqlClient.Routed.csproj",
        "Microsoft.Data.SqlClient.Routed", "Microsoft.Data.SqlClient")]
    public void PatchedProjectNamesMatchRestoreIdentityWithoutChangingClrNames(string projectPath, string packageId, string assemblyName)
    {
        var project = LoadProject(projectPath);
        Assert.Equal(packageId, Path.GetFileNameWithoutExtension(projectPath));
        Assert.Equal(packageId, Assert.Single(project.Descendants("PackageId")).Value);
        Assert.Equal(assemblyName, Assert.Single(project.Descendants("AssemblyName")).Value);
    }

    [Theory]
    [InlineData("src/GhostShell.Previews/GhostShell.Previews.csproj", "SharpCompress")]
    [InlineData("src/GhostShell.Infrastructure/GhostShell.Infrastructure.csproj", "SSH.NET")]
    [InlineData("src/GhostShell.Files/GhostShell.Files.csproj", "SSH.NET")]
    public void UnmodifiedDependenciesUseCentrallyPinnedPackages(string projectPath, string packageId)
    {
        var project = LoadProject(projectPath);
        var reference = Assert.Single(project.Descendants("PackageReference"), element =>
            string.Equals((string?)element.Attribute("Include"), packageId, StringComparison.Ordinal));
        Assert.Null(reference.Attribute("Version"));
        Assert.DoesNotContain(References(project, "ProjectReference"), path =>
            path.Contains("vendor/sshnet/", StringComparison.Ordinal)
            || path.Contains("vendor/sharpcompress/", StringComparison.Ordinal));
    }

    [Fact]
    public void CoreHasOnlyBclDependencies()
    {
        var project = LoadProject("src/GhostShell.Core/GhostShell.Core.csproj");
        Assert.Empty(References(project, "ProjectReference"));
        Assert.Empty(References(project, "PackageReference"));
    }

    [Theory]
    [InlineData("src/GhostShell.Application/GhostShell.Application.csproj")]
    [InlineData("src/GhostShell.Protocol/GhostShell.Protocol.csproj")]
    public void ApplicationAndProtocolReferenceOnlyCore(string projectPath)
    {
        var references = References(LoadProject(projectPath), "ProjectReference");
        var reference = Assert.Single(references);
        Assert.EndsWith("GhostShell.Core.csproj", reference, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationDoesNotReferenceConcreteEnginesOrHost()
    {
        var references = References(
            LoadProject("src/GhostShell.App/GhostShell.App.csproj"),
            "ProjectReference");
        Assert.All(references, reference => Assert.Contains(
            Path.GetFileName(reference.Replace('\\', Path.DirectorySeparatorChar)),
            [
                "GhostShell.Application.csproj",
                "GhostShell.Core.csproj",
                "GhostShell.Docking.csproj",
            ], StringComparer.Ordinal));
        Assert.DoesNotContain(references, reference =>
            reference.Contains("Terminal", StringComparison.Ordinal)
            || reference.Contains("Browser", StringComparison.Ordinal)
            || reference.Contains("SessionHost", StringComparison.Ordinal)
            || reference.Contains("Protocol", StringComparison.Ordinal)
            || reference.Contains("Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void PresentationSourceContainsNoConcreteTerminalOrBrowserImplementationTypes()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src/GhostShell.App");
        var files = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("GhostShell.Terminal", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Ghostty", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GhosttyTerminalHandle", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Avalonia.Controls.WebView", source, StringComparison.Ordinal);
            Assert.DoesNotContain("NativeWebView", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WKWebView", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WebView2", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WPEWebKit", source, StringComparison.Ordinal);
            Assert.DoesNotContain("WebKitGTK", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Exclr8Cef", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CefBrowser", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BrowserAdapterKeepsItsReviewedApplicationCefAndContentDependencies()
    {
        var project = LoadProject("src/GhostShell.Browser/GhostShell.Browser.csproj");
        var projectReferences = References(project, "ProjectReference")
            .Select(reference => reference.Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, projectReferences.Length);
        Assert.Contains(
            projectReferences,
            reference => reference.EndsWith(
                "GhostShell.Application/GhostShell.Application.csproj",
                StringComparison.Ordinal));
        Assert.Contains(
            projectReferences,
            reference => reference.EndsWith(
                "vendor/exclr8cef/src/Exclr8Cef.WebView/Exclr8Cef.WebView.csproj",
                StringComparison.Ordinal));
        Assert.Equal(
            ["FluentIcons.Avalonia", "ReverseMarkdown"],
            References(project, "PackageReference"),
            StringComparer.Ordinal);
    }

    [Fact]
    public void InfrastructureDependsOnlyOnApplicationAndCoreProjects()
    {
        var projectReferences = References(
            LoadProject("src/GhostShell.Infrastructure/GhostShell.Infrastructure.csproj"),
            "ProjectReference");
        var references = projectReferences
            .Select(reference => Path.GetFileName(
                reference.Replace('\\', Path.DirectorySeparatorChar)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            ["GhostShell.Application.csproj", "GhostShell.Core.csproj"],
            references.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    [Fact]
    public void FilesBoundaryDoesNotDependOnPresentationRuntimeOrInfrastructureProjects()
    {
        var projectReferences = References(
                LoadProject("src/GhostShell.Files/GhostShell.Files.csproj"),
                "ProjectReference");
        var references = projectReferences
            .Select(reference => Path.GetFileName(
                reference.Replace('\\', Path.DirectorySeparatorChar)))
            .ToArray();

        Assert.All(references, reference => Assert.Contains(
            reference,
            ["GhostShell.Application.csproj", "GhostShell.Core.csproj"], StringComparer.Ordinal));

        var sourceRoot = Path.Combine(RepositoryRoot, "src/GhostShell.Files");
        var banned = new[]
        {
            "Avalonia",
            "Ghostty",
            "WKWebView",
            "WebView2",
        };
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file);
            Assert.All(banned, value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MonitoringBoundaryDependsOnlyOnApplicationAndCore()
    {
        var references = References(
                LoadProject("src/GhostShell.Monitoring/GhostShell.Monitoring.csproj"),
                "ProjectReference")
            .Select(reference => Path.GetFileName(
                reference.Replace('\\', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "GhostShell.Application.csproj", "GhostShell.Core.csproj" },
            references);

        var project = LoadProject("src/GhostShell.Monitoring/GhostShell.Monitoring.csproj");
        Assert.Empty(References(project, "PackageReference"));
        var sourceRoot = Path.Combine(RepositoryRoot, "src/GhostShell.Monitoring");
        foreach (var file in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Avalonia", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Ghostty", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Management", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DatabasesBoundaryDependsOnlyOnApplicationCoreAndAdoDrivers()
    {
        var projectReferences = References(
                LoadProject("src/GhostShell.Databases/GhostShell.Databases.csproj"),
                "ProjectReference");
        // The pinned provider keeps SQL redirects inside the captured route.
        Assert.Contains(
            "../../vendor/sqlclient/upstream/src/Microsoft.Data.SqlClient/netcore/src/Microsoft.Data.SqlClient.Routed.csproj",
            projectReferences,
            StringComparer.Ordinal);
        var references = projectReferences
            .Select(reference => Path.GetFileName(
                reference.Replace('\\', Path.DirectorySeparatorChar)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "GhostShell.Application.csproj", "GhostShell.Core.csproj", "Microsoft.Data.SqlClient.Routed.csproj" },
            references);

        var sourceRoot = Path.Combine(RepositoryRoot, "src/GhostShell.Databases");
        foreach (var file in Directory.EnumerateFiles(
                     sourceRoot,
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotContain("Avalonia", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Ghostty", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProtocolContainsNoUiNativeOrProviderPayloadTypes()
    {
        var sourceRoot = Path.Combine(RepositoryRoot, "src/GhostShell.Protocol");
        var banned = new[]
        {
            "Avalonia",
            "Ghostty",
            "WKWebView",
            "WebView2",
            "Anthropic",
            "OpenAI",
        };
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file);
            Assert.All(banned, value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DatabaseBackendHasNoDesktopOrRenderingDependencies()
    {
        var project = LoadProject("src/GhostShell.DatabaseBackend/GhostShell.DatabaseBackend.csproj");
        Assert.Empty(References(project, "PackageReference"));
        var references = References(project, "ProjectReference");
        Assert.All(references, reference => Assert.Contains(
            Path.GetFileName(reference.Replace('\\', Path.DirectorySeparatorChar)),
            [
                "GhostShell.Application.csproj",
                "GhostShell.Core.csproj",
                "GhostShell.Databases.csproj",
                "GhostShell.Files.csproj",
                "GhostShell.Infrastructure.csproj",
            ], StringComparer.Ordinal));
    }

    [Fact]
    public void DesktopCompositionOwnsConcreteEngineAndHostReferences()
    {
        var references = References(
            LoadProject("src/GhostShell.Desktop/GhostShell.Desktop.csproj"),
            "ProjectReference");
        Assert.Contains(references, reference => reference.EndsWith(
            "GhostShell.Terminal.csproj",
            StringComparison.Ordinal));
        Assert.Contains(references, reference => reference.EndsWith(
            "GhostShell.Browser.csproj",
            StringComparison.Ordinal));
        Assert.Contains(references, reference => reference.EndsWith(
            "GhostShell.SessionHost.csproj",
            StringComparison.Ordinal));
        Assert.Contains(references, reference => reference.EndsWith(
            "GhostShell.Infrastructure.csproj",
            StringComparison.Ordinal));
        Assert.Contains(references, reference => reference.EndsWith(
            "GhostShell.Monitoring.csproj",
            StringComparison.Ordinal));
        Assert.Contains(references, reference => reference.EndsWith(
            "GhostShell.Agent.Providers.csproj",
            StringComparison.Ordinal));
    }

    [Fact]
    public void AvaloniaPackagesShareASecurityPatchedVersion()
    {
        var versions = new[]
            {
                "src/GhostShell.App/GhostShell.App.csproj",
                "src/GhostShell.Browser/GhostShell.Browser.csproj",
                "src/GhostShell.Desktop/GhostShell.Desktop.csproj",
            }
            .SelectMany(path => LoadProject(path).Descendants("PackageReference"))
            .Where(element => IsAvaloniaFrameworkPackage((string?)element.Attribute("Include")))
            .Select(element => (string?)element.Attribute("Version"))
            .Where(version => version is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Every one of them clears the floor, whatever its cadence.
        foreach (var candidate in versions)
        {
            var parsed = Version.Parse(candidate);
            Assert.True(
                parsed >= new Version(12, 0, 1),
                $"Avalonia {parsed} predates the DBus security fix shipped in 12.0.1.");
        }

        // And the ones Avalonia releases together stay together.
        var lockstep = new[]
            {
                "src/GhostShell.App/GhostShell.App.csproj",
                "src/GhostShell.Browser/GhostShell.Browser.csproj",
                "src/GhostShell.Desktop/GhostShell.Desktop.csproj",
            }
            .SelectMany(path => LoadProject(path).Descendants("PackageReference"))
            .Where(element =>
                IsAvaloniaFrameworkPackage((string?)element.Attribute("Include"))
                && !IndependentlyReleasedAvaloniaPackages.Contains(
                    (string?)element.Attribute("Include"),
                    StringComparer.Ordinal))
            .Select(element => CentralPackageVersion(
                (string?)element.Attribute("Include")
                ?? throw new InvalidDataException("PackageReference is missing Include.")))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(lockstep);
    }

    /// <summary>
    /// Avalonia framework packages that do not ship on the framework's own
    /// release train. DataGrid stopped at 12.0.1 while the framework went on
    /// to 12.0.5; its package dependencies explicitly resolve Avalonia 12.0.5,
    /// so holding the framework back to match its package label would lose
    /// later fixes for nothing.
    /// </summary>
    private static readonly string[] IndependentlyReleasedAvaloniaPackages =
    [
        "Avalonia.Controls.DataGrid",
    ];

    /// <summary>
    /// The rule is about the Avalonia framework's own packages, which ship as
    /// one version. Third-party control libraries whose names merely begin with
    /// "Avalonia" — the source editor's packages — track the framework's major
    /// on their own release cadence and would break the single-version rule
    /// without saying anything about the DBus fix.
    /// </summary>
    private static readonly string[] NonFrameworkAvaloniaPackages =
    [
        "Avalonia.AvaloniaEdit",
        "AvaloniaEdit.TextMate",
    ];

    private static bool IsAvaloniaFrameworkPackage(string? include) =>
        include?.StartsWith("Avalonia", StringComparison.Ordinal) is true
        && !NonFrameworkAvaloniaPackages.Contains(include, StringComparer.Ordinal);

    [Fact]
    public void DesktopUsesAvaloniasDbusProtocolVersionForItsPortalClient()
    {
        var project = LoadProject("src/GhostShell.Desktop/GhostShell.Desktop.csproj");
        var reference = Assert.Single(project.Descendants("PackageReference"), element =>
            string.Equals(
                (string?)element.Attribute("Include"),
                "Tmds.DBus.Protocol",
                StringComparison.Ordinal));

        Assert.Null(reference.Attribute("Version"));
        Assert.Equal("0.92.0", CentralPackageVersion("Tmds.DBus.Protocol"));
    }

    [Fact]
    public void DesktopKeepsDbusSourceGeneratorBuildOnly()
    {
        var project = LoadProject("src/GhostShell.Desktop/GhostShell.Desktop.csproj");
        var reference = Assert.Single(project.Descendants("PackageReference"), element =>
            string.Equals(
                (string?)element.Attribute("Include"),
                "Tmds.DBus.SourceGenerator",
                StringComparison.Ordinal));

        Assert.Equal("all", (string?)reference.Attribute("PrivateAssets"));
    }

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath));

    private static string CentralPackageVersion(string packageId)
    {
        var package = Assert.Single(
            LoadProject("Directory.Packages.props").Descendants("PackageVersion"),
            element => string.Equals(
                (string?)element.Attribute("Include"),
                packageId,
                StringComparison.Ordinal));
        return (string?)package.Attribute("Version")
            ?? throw new InvalidDataException($"Central version is missing for {packageId}.");
    }

    private static IReadOnlyList<string> References(XDocument project, string elementName) =>
        [.. project.Descendants(elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => value is not null)
            .Cast<string>()];

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
}

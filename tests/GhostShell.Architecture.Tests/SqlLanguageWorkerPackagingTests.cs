using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace GhostShell.Architecture.Tests;

public sealed class SqlLanguageWorkerPackagingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void DesktopPublishPreservesTheRidScopedWorkerLocation()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "GhostShell.Desktop.csproj"));
        var worker = Assert.Single(
            project.Descendants("Content"),
            element => string.Equals(
                (string?)element.Attribute("Include"),
                "$(GhostShellSqlLanguageWorkerPath)",
                StringComparison.Ordinal));

        Assert.Equal(
            "runtimes/$(GhostShellEffectiveRuntimeIdentifier)/native/$(GhostShellSqlLanguageWorkerName)",
            (string?)worker.Attribute("Link"));
        Assert.Equal("PreserveNewest", (string?)worker.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)worker.Attribute("CopyToPublishDirectory"));

        var linkedPayload = project.Descendants("Content")
            .Select(element => (string?)element.Attribute("Link"))
            .Where(link => link is not null)
            .ToArray();
        Assert.Contains(
            "runtimes/$(GhostShellEffectiveRuntimeIdentifier)/native/THIRD-PARTY-NOTICES.md",
            linkedPayload, StringComparer.Ordinal);
        Assert.Contains(
            "runtimes/$(GhostShellEffectiveRuntimeIdentifier)/native/runtime-dependencies.txt",
            linkedPayload, StringComparer.Ordinal);
        Assert.Contains(
            "runtimes/$(GhostShellEffectiveRuntimeIdentifier)/native/build-receipt.json",
            linkedPayload, StringComparer.Ordinal);
    }

    [Fact]
    public void ReleasePublishFailsClosedWithoutTheCompleteSupportedRidPayload()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "GhostShell.Desktop.csproj"));

        Assert.Equal(
            "false",
            project.Descendants("GhostShellSqlLanguageRequired").Single().Value);
        var validation = project.Descendants("Target")
            .Single(target => string.Equals(
                (string?)target.Attribute("Name"),
                "ValidateSqlLanguagePayload",
                StringComparison.Ordinal));
        Assert.Contains(
            validation.Elements("Error"),
            error => ((string?)error.Attribute("Text"))?.Contains(
                "win-arm64 is unsupported by GraalVM Native Image",
                StringComparison.Ordinal) == true);

        var requiredFiles = project.Descendants("GhostShellSqlLanguageRequiredFile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .ToArray();
        Assert.Contains("$(GhostShellSqlLanguageWorkerPath)", requiredFiles, StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellSqlLanguageArtifactDirectory)/THIRD-PARTY-NOTICES.md",
            requiredFiles, StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellSqlLanguageArtifactDirectory)/runtime-dependencies.txt",
            requiredFiles, StringComparer.Ordinal);
        Assert.Contains(
            "$(GhostShellSqlLanguageArtifactDirectory)/build-receipt.json",
            requiredFiles, StringComparer.Ordinal);
    }

    [Fact]
    public void MacPackageRequiresTheWorkerBeforeAndAfterBundling()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "package-macos.sh"));

        Assert.Contains(
            "native/artifacts/osx-arm64",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "sql_language_worker=\"${sql_language_artifact_directory}/ghostshell-sql-language\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "publish_dir}/runtimes/osx-arm64/native/ghostshell-sql-language",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate}/Contents/MacOS/runtimes/osx-arm64/native/ghostshell-sql-language",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "publish_dir}/runtimes/osx-arm64/native/THIRD-PARTY-NOTICES.md",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "publish_dir}/runtimes/osx-arm64/native/runtime-dependencies.txt",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "publish_dir}/runtimes/osx-arm64/native/build-receipt.json",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract sha256 raw",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract abi raw",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract minimumOsVersion raw",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract legalClosureFormatVersion raw -expect integer",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract legalDocumentCount raw -expect integer",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract legalReviewRequiredCount raw -expect integer",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract runtimeDependencyCount raw -expect integer",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract runtimeDependenciesSha256 raw -expect string",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract thirdPartyNoticesSha256 raw -expect string",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "plutil -extract mavenContentLockSha256 raw -expect string",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "native/sql-language-worker/maven-content-lock.json",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "sql_language_actual_maven_lock_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "darwin-arm64",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Mach-O 64-bit executable arm64",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "LC_BUILD_VERSION",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "sql_language_platform",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "maximum_sql_language_macos_version=\"13.0\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "sql_language_receipt_minos_normalized",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "macos_version_is_at_most",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmp -s \"${sql_language_receipt}\" \"${published_sql_language_receipt}\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate_sql_language_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate_sql_language_resources}/build-receipt.json",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "published_sql_language_dependencies_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "published_sql_language_notices_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate_sql_language_dependencies_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "candidate_sql_language_notices_sha",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:GhostShellSqlLanguageRequired=true",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Dormant_database_conformance_preserves_the_native_worker_contract()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "database-viewer-integration.yml"));
        var wrapper = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "test-database-viewer-integration.sh"));

        Assert.Equal(
            2,
            workflow.Split("if: ${{ false }}", StringSplitOptions.None).Length - 1);
        Assert.Contains("needs: sql-language-worker", workflow, StringComparison.Ordinal);
        Assert.Contains(
            "GHOSTSHELL_RUN_SQL_LANGUAGE_NATIVE: \"1\"",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "GHOSTSHELL_SQL_LANGUAGE_WORKER: ${{ github.workspace }}/native/artifacts/linux-x64/ghostshell-sql-language",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "Run the real native client lifecycle",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "GHOSTSHELL_SQL_LANGUAGE_WORKER is required when GHOSTSHELL_RUN_SQL_LANGUAGE_NATIVE=1.",
            wrapper,
            StringComparison.Ordinal);
        Assert.Contains("[[ ! -f \"${worker_path}\" ]]", wrapper, StringComparison.Ordinal);
        Assert.Contains("[[ ! -x \"${worker_path}\" ]]", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_gate_does_not_publish_unsupported_portable_platforms()
    {
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "repository-gate.yml"));

        Assert.DoesNotContain("portable-release-publish:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("linux-x64", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("linux-arm64", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("win-x64", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("win-arm64", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlWorkerContainerBuildersArePlatformSpecificAndDigestPinned()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "build-sql-language-worker.sh"));
        var imageDeclarations = script.Split('\n')
            .Where(line =>
                line.StartsWith("readonly MAVEN_IMAGE_LINUX_", StringComparison.Ordinal)
                || line.StartsWith("readonly NATIVE_IMAGE_LINUX_", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, imageDeclarations.Length);
        Assert.All(
            imageDeclarations,
            declaration => Assert.Matches("@sha256:[0-9a-f]{64}\\\"$", declaration));
        var executableImageReferences = Regex.Matches(
                script,
                "(?:maven|container-registry\\.oracle\\.com/graalvm/native-image):[^\\s\\\"']+",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1))
            .Select(match => match.Value)
            .ToArray();
        Assert.Equal(4, executableImageReferences.Length);
        Assert.All(
            executableImageReferences,
            reference => Assert.Matches("@sha256:[0-9a-f]{64}$", reference));
        Assert.Contains("MAVEN_IMAGE_VERSION=\"3.9.11-eclipse-temurin-21\"", script, StringComparison.Ordinal);
        Assert.Contains("NATIVE_IMAGE_VERSION=\"25.0.4\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly MAVEN_IMAGE=", script, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly NATIVE_IMAGE=", script, StringComparison.Ordinal);
        Assert.Contains(
            "native_image_output=\"$(\"$native_image_command\" --version 2>&1)\"",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "native_image_command\" --version 2>&1 | sed -n '1p'",
            script,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            script.Split(
                "awk '/^native-image / { print; exit }'",
                StringSplitOptions.None).Length - 1);
        Assert.All(
            script.Split("docker run", StringSplitOptions.None).Skip(1),
            invocation => Assert.Contains("--network none", invocation, StringComparison.Ordinal));
    }

    [Fact]
    public void SqlWorkerMavenInputsAreCompletelyLockedBeforeOfflineExecution()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "build-sql-language-worker.sh"));
        var preparerInvocation = script.IndexOf(
            "\"$MAVEN_REPOSITORY_PREPARER\" \"$MAVEN_CONTENT_LOCK\"",
            StringComparison.Ordinal);
        var firstMavenExecution = script.IndexOf("mvn -B --offline", StringComparison.Ordinal);

        Assert.True(preparerInvocation >= 0);
        Assert.True(firstMavenExecution > preparerInvocation);
        Assert.Contains("-Dmaven.repo.local=/locked-m2", script, StringComparison.Ordinal);
        Assert.Contains("$locked_maven_repository:/locked-m2:ro", script, StringComparison.Ordinal);
        Assert.Contains("mavenContentLockSha256", script, StringComparison.Ordinal);
        var preparer = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "prepare-locked-maven-repository.py"));
        Assert.Contains("MAVEN_PATH_PATTERN.fullmatch(relative)", preparer, StringComparison.Ordinal);
        Assert.Contains("RejectRedirects()", preparer, StringComparison.Ordinal);
        Assert.Contains("ssl.create_default_context()", preparer, StringComparison.Ordinal);
        var repositorySeal = preparer.IndexOf("seal_repository(destination)", StringComparison.Ordinal);
        var successfulPreparation = preparer.IndexOf(
            "print(f\"Verified {len(artifacts)} Maven files in {destination}\")",
            StringComparison.Ordinal);
        Assert.True(repositorySeal >= 0);
        Assert.True(successfulPreparation > repositorySeal);
        Assert.Contains("make_read_only(path, READ_ONLY_FILE_MODE)", preparer, StringComparison.Ordinal);
        Assert.Contains("make_read_only(root_path, READ_ONLY_DIRECTORY_MODE)", preparer, StringComparison.Ordinal);
        Assert.Contains("actual_mode & WRITE_MODE_MASK", preparer, StringComparison.Ordinal);
        Assert.Contains("WINDOWS_READ_ONLY_PRINCIPAL", preparer, StringComparison.Ordinal);
        Assert.Contains("(OI)(CI)(W,D,DC)", preparer, StringComparison.Ordinal);
        Assert.Contains("--unseal", preparer, StringComparison.Ordinal);
        Assert.Contains(
            "\"$MAVEN_REPOSITORY_PREPARER\" --unseal \"$locked_maven_repository\"",
            script,
            StringComparison.Ordinal);

        using var lockDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "native",
            "sql-language-worker",
            "maven-content-lock.json")));
        var root = lockDocument.RootElement;
        Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal(
            "https://repo.maven.apache.org/maven2/",
            root.GetProperty("repository").GetString());
        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.True(artifacts.Length > 100);
        var paths = artifacts
            .Select(artifact => artifact.GetProperty("path").GetString()!)
            .ToArray();
        Assert.Equal(paths.Order(StringComparer.Ordinal), paths, StringComparer.Ordinal);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            artifacts,
            artifact =>
            {
                Assert.True(artifact.GetProperty("size").GetInt64() > 0);
                Assert.Matches("^[0-9a-f]{64}$", artifact.GetProperty("sha256").GetString()!);
                Assert.True(
                    artifact.GetProperty("path").GetString()!.EndsWith(".jar", StringComparison.Ordinal)
                    || artifact.GetProperty("path").GetString()!.EndsWith(".pom", StringComparison.Ordinal));
            });
        Assert.Contains(
            "org/apache/calcite/calcite-core/1.42.0/calcite-core-1.42.0.jar",
            paths,
            StringComparer.Ordinal);
        Assert.Contains(
            "com/fasterxml/jackson/jackson-bom/2.18.9/jackson-bom-2.18.9.pom",
            paths,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            paths,
            path => path.EndsWith("jackson-databind-2.18.6.jar", StringComparison.Ordinal)
                || path.EndsWith("jackson-core-2.18.6.jar", StringComparison.Ordinal)
                || path.EndsWith("commons-lang3-3.1.jar", StringComparison.Ordinal)
                || path.EndsWith("httpclient5-5.5.jar", StringComparison.Ordinal)
                || path.EndsWith("httpcore5-5.3.5.jar", StringComparison.Ordinal)
                || path.EndsWith("httpcore5-h2-5.3.4.jar", StringComparison.Ordinal));
        Assert.Contains(
            "org/apache/maven/plugins/maven-dependency-plugin/3.8.1/maven-dependency-plugin-3.8.1.jar",
            paths,
            StringComparer.Ordinal);
        Assert.Contains(
            "org/apache/maven/surefire/surefire-junit-platform/3.5.3/surefire-junit-platform-3.5.3.jar",
            paths,
            StringComparer.Ordinal);

        var pom = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "native",
            "sql-language-worker",
            "pom.xml"));
        var xmlNamespace = pom.Root!.GetDefaultNamespace();
        var plugins = pom.Descendants(xmlNamespace + "plugin").ToArray();
        Assert.NotEmpty(plugins);
        Assert.All(
            plugins,
            plugin => Assert.False(string.IsNullOrWhiteSpace(plugin.Element(xmlNamespace + "version")?.Value)));
        var pluginIds = plugins
            .Select(plugin => plugin.Element(xmlNamespace + "artifactId")!.Value)
            .ToArray();
        foreach (var pluginId in new[]
                 {
                     "maven-clean-plugin",
                     "maven-resources-plugin",
                     "maven-compiler-plugin",
                     "maven-surefire-plugin",
                     "maven-jar-plugin",
                     "maven-shade-plugin",
                     "maven-dependency-plugin",
                     "maven-install-plugin",
                     "maven-deploy-plugin",
                     "maven-site-plugin",
                 })
        {
            Assert.Contains(pluginId, pluginIds, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void SqlWorkerRoutesLocalMavenOutputsOutsideASealedSource()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "scripts",
            "build-sql-language-worker.sh"));
        var project = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "native",
            "sql-language-worker",
            "pom.xml"));
        var workflow = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            ".github",
            "workflows",
            "repository-gate.yml"));

        Assert.Contains(
            "worker_build_directory=\"$GHOSTSHELL_BUILD_ARTIFACTS_ROOT/sql-language-worker/$rid\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"-Dghostshell.build.directory=$worker_build_directory\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "<directory>${ghostshell.build.directory}</directory>",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"$WORKER_DIRECTORY/target/",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "${sealed_source}/native/sql-language-worker/target",
            workflow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EverySupportedSelfContainedPackageUsesTheServicedRuntimePack()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "GhostShell.Desktop.csproj"));
        Assert.Equal("10.0.11", project.Descendants("GhostShellRuntimePackVersion").Single().Value);
        Assert.Contains(
            "$(GhostShellRuntimePackVersion)",
            project.Descendants("GhostShellRuntimePackDirectory").Single().Value,
            StringComparison.Ordinal);
        var runtimeValidation = project.Descendants("Target")
            .Single(target => string.Equals(
                target.Attribute("Name")?.Value,
                "ValidateServicedRuntimePack",
                StringComparison.Ordinal));
        Assert.Equal("ProcessFrameworkReferences", runtimeValidation.Attribute("AfterTargets")?.Value);
        Assert.Contains("$(SelfContained)", runtimeValidation.Attribute("Condition")?.Value, StringComparison.Ordinal);
        Assert.Contains(
            "%(GhostShellSelectedRuntimePack.NuGetPackageVersion)' != '$(GhostShellRuntimePackVersion)",
            runtimeValidation.ToString(),
            StringComparison.Ordinal);
        using var globalConfiguration = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "global.json")));
        Assert.Equal(
            "10.0.303",
            globalConfiguration.RootElement.GetProperty("sdk").GetProperty("version").GetString());
        Assert.Contains(
            "sdk_version=\"10.0.303\"",
            File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "bootstrap.sh")),
            StringComparison.Ordinal);
        var canonicalGate = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "check.sh"));
        Assert.Contains(
            "\"${dotnet}\" restore GhostShell.slnx --locked-mode",
            canonicalGate,
            StringComparison.Ordinal);
        using var aotLock = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "tools",
            "GhostShell.SqlLanguageAotProbe",
            "packages.lock.json")));
        var linkerTasks = aotLock.RootElement
            .GetProperty("dependencies")
            .GetProperty("net10.0")
            .GetProperty("Microsoft.NET.ILLink.Tasks");
        Assert.Equal("[10.0.11, )", linkerTasks.GetProperty("requested").GetString());
        Assert.Equal("10.0.11", linkerTasks.GetProperty("resolved").GetString());

        var supportedRids = new[] { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64" };
        var sourceRoot = Path.Combine(RepositoryRoot, "src");
        var referenceProjects = Directory.GetFiles(
                sourceRoot,
                $"packages.{supportedRids[0]}.lock.json",
                SearchOption.AllDirectories)
            .Select(path => Path.GetDirectoryName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(referenceProjects);
        foreach (var rid in supportedRids)
        {
            var lockedProjects = Directory.GetFiles(
                    sourceRoot,
                    $"packages.{rid}.lock.json",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetDirectoryName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(referenceProjects, lockedProjects, StringComparer.Ordinal);
            using var lockDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                RepositoryRoot,
                "src",
                "GhostShell.Desktop",
                $"packages.{rid}.lock.json")));
            Assert.Equal(2, lockDocument.RootElement.GetProperty("version").GetInt32());
            var targetFramework = string.Equals(rid, "win-x64", StringComparison.Ordinal)
                ? "net10.0-windows10.0.19041"
                : "net10.0";
            Assert.Contains(
                $"{targetFramework}/{rid}",
                lockDocument.RootElement.GetProperty("dependencies").EnumerateObject()
                    .Select(dependency => dependency.Name),
                StringComparer.Ordinal);
        }

        var buildTargets = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.targets"));
        Assert.Contains(
            "RequireExistingRuntimeLockForLockedRestore",
            buildTargets.Root?.Attribute("InitialTargets")?.Value,
            StringComparison.Ordinal);
        var restoreGuard = buildTargets.Descendants("Target").Single(target => string.Equals(
            target.Attribute("Name")?.Value,
            "RequireExistingRuntimeLockForLockedRestore",
            StringComparison.Ordinal));
        Assert.Contains("_GenerateRestoreGraphProjectEntry", restoreGuard.Attribute("BeforeTargets")?.Value);
        Assert.Contains("$(RestoreLockedMode)", restoreGuard.Attribute("Condition")?.Value);
        Assert.Contains("$(RuntimeIdentifiers)", restoreGuard.Attribute("Condition")?.Value);
        Assert.Contains(
            "$(MSBuildThisFileDirectory)src/GhostShell.*/*.csproj",
            restoreGuard.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("GhostShellExpectedRuntimeLock", restoreGuard.ToString(), StringComparison.Ordinal);
        Assert.Contains("System.IO.Path", restoreGuard.ToString(), StringComparison.Ordinal);
        Assert.Contains("packages.$(RuntimeIdentifier).lock.json", restoreGuard.ToString(), StringComparison.Ordinal);
        Assert.Contains("packages.$(RuntimeIdentifiers).lock.json", restoreGuard.ToString(), StringComparison.Ordinal);
        Assert.Contains("GhostShellRequiredRuntimeLock", restoreGuard.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            restoreGuard.Descendants("Error"),
            error => error.Attribute("Condition")?.Value.Contains(
                "'$(NuGetLockFilePath)' == ''",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            restoreGuard.Descendants("Error"),
            error => error.Attribute("Condition")?.Value.Contains(
                "@(GhostShellSelectedRuntimeLock)' != '@(GhostShellExpectedRuntimeLock)",
                StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            "%(Directory)$(NuGetLockFilePath)",
            restoreGuard.ToString(),
            StringComparison.Ordinal);
        var publishGuard = buildTargets.Descendants("Target").Single(target => string.Equals(
            target.Attribute("Name")?.Value,
            "RequireLockedRestoreForSelfContainedDesktopPackage",
            StringComparison.Ordinal));
        Assert.Contains("Publish", publishGuard.Attribute("BeforeTargets")?.Value);
        Assert.Equal(
            "RequireExistingRuntimeLockForLockedRestore",
            publishGuard.Attribute("DependsOnTargets")?.Value);
        Assert.DoesNotContain(
            "$(NuGetLockFilePath)",
            publishGuard.Attribute("Condition")?.Value,
            StringComparison.Ordinal);
        Assert.Contains(
            publishGuard.Descendants("Error"),
            error => error.Attribute("Condition")?.Value.Contains(
                "'$(RuntimeIdentifier)' == ''",
                StringComparison.Ordinal) == true);
        Assert.Contains(
            publishGuard.Descendants("Error"),
            error => error.Attribute("Condition")?.Value.Contains(
                "'$(RestoreLockedMode)' != 'true'",
                StringComparison.Ordinal) == true);

        var macPackageScript = File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "package-macos.sh"));
        Assert.Contains("packages.${runtime_identifier}.lock.json", macPackageScript, StringComparison.Ordinal);
        Assert.Contains("--locked-mode", macPackageScript, StringComparison.Ordinal);
        Assert.Contains("--no-restore", macPackageScript, StringComparison.Ordinal);
        Assert.Contains("-p:RestoreLockedMode=true", macPackageScript, StringComparison.Ordinal);

        var supportedRidCondition = project.Descendants("GhostShellSqlLanguageSupported")
            .Single()
            .Attribute("Condition")!
            .Value;
        foreach (var rid in supportedRids)
        {
            Assert.Contains(rid, supportedRidCondition, StringComparison.Ordinal);
        }

        var releaseInputs = string.Concat(
            project.ToString(),
            File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "package-macos.sh")),
            File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "repository-gate.yml")),
            File.ReadAllText(Path.Combine(RepositoryRoot, "licenses", "managed-components.json")));
        Assert.DoesNotContain(
            "runtimepack.Microsoft.NETCore.App.Runtime.osx-arm64/10.0.10",
            releaseInputs,
            StringComparison.Ordinal);
        using var managedCatalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "licenses",
            "managed-components.json")));
        Assert.Equal(
            2,
            managedCatalog.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "GhostSHELL ${productVersion} managed-component evidence",
            managedCatalog.RootElement.GetProperty("documentName").GetString());
        var firstPartyProjects = managedCatalog.RootElement
            .GetProperty("dependencies")
            .EnumerateArray()
            .Where(dependency =>
                string.Equals(
                    dependency.GetProperty("kind").GetString(),
                    "project",
                    StringComparison.Ordinal)
                && dependency.GetProperty("identity").GetString() is { } identity
                && (identity.StartsWith("GhostShell.", StringComparison.Ordinal)
                    || identity.StartsWith("GhostShell/", StringComparison.Ordinal)))
            .ToArray();
        Assert.NotEmpty(firstPartyProjects);
        Assert.All(
            firstPartyProjects,
            dependency => Assert.EndsWith(
                "/${productVersion}",
                dependency.GetProperty("identity").GetString(),
                StringComparison.Ordinal));
        var runtimeEvidence = managedCatalog.RootElement.GetProperty("dependencies").EnumerateArray()
            .Where(dependency => string.Equals(
                dependency.GetProperty("depsType").GetString(),
                "runtimepack",
                StringComparison.Ordinal)).ToArray();
        Assert.Equal(
            ["runtimepack.Microsoft.AspNetCore.App.Runtime.osx-arm64/10.0.11", "runtimepack.Microsoft.NETCore.App.Runtime.osx-arm64/10.0.11"],
            runtimeEvidence.Select(item => item.GetProperty("identity").GetString()).Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.All(runtimeEvidence, item =>
        {
            Assert.Equal("runtimepack." + item.GetProperty("nuGetId").GetString() + "/10.0.11",
                item.GetProperty("identity").GetString());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("contentHash").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("nupkgSha512").GetString()));
        });
    }

    [Theory]
    [InlineData("", "requires NuGetLockFilePath")]
    [InlineData("packages.lock.json", "requires the reviewed runtime lock path")]
    [InlineData("packages.linux-x64.lock.json", "requires the reviewed runtime lock path")]
    public async Task SelfContainedLockGuardRejectsGlobalPathOverrides(
        string lockPath,
        string expectedError)
    {
        var result = await RunSelfContainedLockGuardAsync("osx-arm64", lockPath);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(expectedError, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    [InlineData("win-x64")]
    public async Task SelfContainedLockGuardAcceptsEachExactSupportedRidLock(string runtimeIdentifier)
    {
        var result = await RunSelfContainedLockGuardAsync(
            runtimeIdentifier,
            $"packages.{runtimeIdentifier}.lock.json");

        Assert.True(
            result.ExitCode == 0,
            $"Exact {runtimeIdentifier} lock was rejected.{Environment.NewLine}{result.Output}");
    }

    [Fact]
    public void ManagedProtocolUsesGeneratedJsonMetadata()
    {
        var protocol = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Infrastructure",
            "SqlLanguageWorkerProtocol.cs"));

        Assert.Contains("JsonSerializerContext", protocol, StringComparison.Ordinal);
        Assert.Contains("[JsonSerializable(typeof(WorkerRequestEnvelope))]", protocol, StringComparison.Ordinal);
        Assert.Contains("[JsonSerializable(typeof(WorkerResponseEnvelope))]", protocol, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializerOptions", protocol, StringComparison.Ordinal);
        Assert.DoesNotContain("GetType(", protocol, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotProbeCompilesTheExactProductionClientSources()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "tools",
            "GhostShell.SqlLanguageAotProbe",
            "GhostShell.SqlLanguageAotProbe.csproj"));
        Assert.Equal(
            "true",
            project.Descendants("IsAotCompatible").Single().Value);

        var linkedSources = project.Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => include is not null)
            .ToArray();
        Assert.Contains(
            "../../src/GhostShell.Infrastructure/CalciteSqlLanguageService.cs",
            linkedSources, StringComparer.Ordinal);
        Assert.Contains(
            "../../src/GhostShell.Infrastructure/CalciteSqlLanguageSession.cs",
            linkedSources, StringComparer.Ordinal);
        Assert.Contains(
            "../../src/GhostShell.Infrastructure/SqlLanguageWorkerProtocol.cs",
            linkedSources, StringComparer.Ordinal);
        Assert.Contains(
            "../../src/GhostShell.Infrastructure/UnavailableSqlLanguageSession.cs",
            linkedSources, StringComparer.Ordinal);
    }

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

        throw new DirectoryNotFoundException(
            "Unable to locate the GhostSHELL repository root.");
    }

    private static async Task<MsBuildResult> RunSelfContainedLockGuardAsync(
        string runtimeIdentifier,
        string lockPath)
    {
        var dotnet = Path.Combine(
            RepositoryRoot,
            ".dotnet",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var project = Path.Combine(
            RepositoryRoot,
            "src",
            "GhostShell.Desktop",
            "GhostShell.Desktop.csproj");
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            WorkingDirectory = RepositoryRoot,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("msbuild");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-target:RequireLockedRestoreForSelfContainedDesktopPackage");
        start.ArgumentList.Add($"-property:RuntimeIdentifier={runtimeIdentifier}");
        start.ArgumentList.Add("-property:SelfContained=true");
        start.ArgumentList.Add("-property:RestoreLockedMode=true");
        start.ArgumentList.Add($"-property:NuGetLockFilePath={lockPath}");
        start.ArgumentList.Add("-verbosity:quiet");
        start.ArgumentList.Add("-nologo");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The MSBuild lock-policy probe did not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return new MsBuildResult(
            process.ExitCode,
            string.Concat(await standardOutput, await standardError));
    }

    private sealed record MsBuildResult(int ExitCode, string Output);
}

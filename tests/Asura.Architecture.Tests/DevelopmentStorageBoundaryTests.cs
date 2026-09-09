using System.Diagnostics;

namespace Asura.Architecture.Tests;

public sealed class DevelopmentStorageBoundaryTests
{
    [Fact]
    public void Only_packaging_explicitly_selects_production_storage()
    {
        var props = Read("Directory.Build.props");
        Assert.Contains("'$(AsuraProductionBuild)' == 'true'", props, StringComparison.Ordinal);
        Assert.Contains("$(DefineConstants);ASURA_PRODUCTION", props, StringComparison.Ordinal);
        var packaging = Read("scripts/package-macos.sh");
        Assert.Equal(3, packaging.Split("-p:AsuraProductionBuild=true", StringSplitOptions.None).Length - 1);
        var development = Read("scripts/run-macos-development.sh");
        Assert.DoesNotContain("AsuraProductionBuild=true", development, StringComparison.Ordinal);
        Assert.Contains("CFBundleIdentifier -string sh.asura.development", development, StringComparison.Ordinal);
        Assert.Contains("CFBundleDisplayName -string \"Asura Development\"", development, StringComparison.Ordinal);
    }

    [Fact]
    public void Rehearsal_never_changes_the_users_default_keychain()
    {
        var rehearsal = Read("scripts/rehearse-macos-release.sh");
        Assert.DoesNotContain("security default-keychain", rehearsal, StringComparison.Ordinal);
        Assert.Contains("--keychain \"${signing_keychain}\"", rehearsal, StringComparison.Ordinal);
        Assert.Contains("security set-keychain-settings -u -t 21600", rehearsal, StringComparison.Ordinal);
        var unlock = rehearsal.LastIndexOf("security unlock-keychain", StringComparison.Ordinal);
        var package = rehearsal.IndexOf("./scripts/package-macos-github-release.sh", StringComparison.Ordinal);
        Assert.True(unlock > 0 && unlock < package);
    }

    [Fact]
    public void Rehearsal_executes_only_a_fresh_copy_of_the_verified_scanner_archive()
    {
        var rehearsal = Read("scripts/rehearse-macos-release.sh");
        Assert.Contains("grype_execution_directory=\"$(mktemp -d \"${working_directory}/grype.XXXXXX\")\"", rehearsal, StringComparison.Ordinal);
        Assert.Contains("grype_executable=\"${grype_execution_directory}/grype\"", rehearsal, StringComparison.Ordinal);
        Assert.DoesNotContain("if [[ ! -x \"${grype_executable}\" ]]", rehearsal, StringComparison.Ordinal);
        var checksum = rehearsal.IndexOf("bfcefa3f3b1690d9c77d847841b32ebd6106ab0e0e32f810924707e704d53584", StringComparison.Ordinal);
        var extraction = rehearsal.IndexOf("tar -xzf \"${grype_archive}\" -C \"${grype_execution_directory}\" grype", StringComparison.Ordinal);
        var execution = rehearsal.IndexOf("    \"${grype_executable}\"", StringComparison.Ordinal);
        Assert.True(checksum >= 0 && checksum < extraction && extraction < execution);
        Assert.Contains("export -n APPLE_CERTIFICATE_P12_BASE64 APPLE_CERTIFICATE_PASSWORD", rehearsal, StringComparison.Ordinal);
        var initializeSigning = rehearsal.IndexOf("\nprepare_signing_keychain\n", StringComparison.Ordinal);
        Assert.True(initializeSigning > execution);
    }

    [Fact]
    public void Pages_limits_deployment_to_main_and_keeps_write_permissions_out_of_the_build()
    {
        var workflow = Read(".github/workflows/website.yml");
        var build = workflow.IndexOf("  build:", StringComparison.Ordinal);
        var deploy = workflow.IndexOf("  deploy:", StringComparison.Ordinal);
        Assert.True(build > 0 && deploy > build);
        Assert.DoesNotContain("pages: write", workflow[..deploy], StringComparison.Ordinal);
        Assert.DoesNotContain("id-token: write", workflow[..deploy], StringComparison.Ordinal);
        Assert.Contains("if: github.ref == 'refs/heads/main'", workflow[build..deploy], StringComparison.Ordinal);
        Assert.Contains("if: github.ref == 'refs/heads/main'", workflow[deploy..], StringComparison.Ordinal);
        Assert.Contains("pages: write", workflow[deploy..], StringComparison.Ordinal);
        Assert.Contains("id-token: write", workflow[deploy..], StringComparison.Ordinal);
    }

    [Fact]
    public void Rehearsal_credentials_are_not_inherited_by_subprocesses()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var rehearsal = Read("scripts/rehearse-macos-release.sh");
        var setupEnd = rehearsal.IndexOf("\nscript_dir=", StringComparison.Ordinal);
        Assert.True(setupEnd > 0);
        // Execute only the environment-isolation preamble, never the rehearsal
        // body. The child starts with synthetic values and no inherited secrets.
        var start = new ProcessStartInfo("/bin/bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Clear();
        foreach (var name in new[]
                 {
                     "APPLE_CERTIFICATE_P12_BASE64",
                     "APPLE_CERTIFICATE_PASSWORD",
                     "APPLE_DEVELOPER_ID_APPLICATION",
                     "APPLE_NOTARY_ISSUER_ID",
                     "APPLE_NOTARY_KEY_ID",
                     "APPLE_NOTARY_PRIVATE_KEY_BASE64",
                 })
        {
            start.Environment[name] = "synthetic-release-credential";
        }
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(rehearsal[..setupEnd] + "\n/usr/bin/env");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        Assert.DoesNotContain("APPLE_", output, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-release-credential", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rehearsal_hands_local_credentials_to_signing_commands_explicitly()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var rehearsal = Read("scripts/rehearse-macos-release.sh");
        var preamble = rehearsal[..rehearsal.IndexOf("\nscript_dir=", StringComparison.Ordinal)];
        var preparationStart = rehearsal.IndexOf("prepare_signing_keychain() {", StringComparison.Ordinal);
        var preparationEnd = rehearsal.IndexOf("\n}\n", preparationStart, StringComparison.Ordinal) + 3;
        var packageStart = rehearsal.IndexOf("./scripts/package-macos-github-release.sh ", StringComparison.Ordinal);
        var packageEnd = rehearsal.IndexOf("\n\n", packageStart, StringComparison.Ordinal);
        var package = rehearsal[packageStart..packageEnd].Replace(
            "./scripts/package-macos-github-release.sh", "package_stub", StringComparison.Ordinal);
        var directory = Directory.CreateTempSubdirectory("asura-signing-handoff-").FullName;
        try
        {
            var start = new ProcessStartInfo("/bin/bash")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/bin:/bin";
            start.Environment["APPLE_CERTIFICATE_P12_BASE64"] = Convert.ToBase64String("synthetic certificate"u8);
            start.Environment["APPLE_NOTARY_PRIVATE_KEY_BASE64"] = Convert.ToBase64String("synthetic notary key"u8);
            start.Environment["APPLE_CERTIFICATE_PASSWORD"] = "synthetic password";
            start.Environment["APPLE_DEVELOPER_ID_APPLICATION"] = "synthetic identity";
            start.Environment["APPLE_NOTARY_KEY_ID"] = "synthetic key id";
            start.Environment["APPLE_NOTARY_ISSUER_ID"] = "synthetic issuer";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(preamble + "\n" + rehearsal[preparationStart..preparationEnd] + "\n" + """
                signing_directory="$1"
                signing_keychain="$1/test.keychain"
                signing_password='synthetic keychain password'
                signing_identity="${APPLE_DEVELOPER_ID_APPLICATION}"
                notary_profile='synthetic profile'
                previous_keychains=()
                security() {
                    printf 'security'; printf ' <%s>' "$@"; printf '\n' >&2
                    if [[ "$1" == find-identity ]]; then printf '%s\n' "$signing_identity"; fi
                }
                xcrun() { printf 'xcrun'; printf ' <%s>' "$@"; printf '\n'; }
                package_stub() { printf 'package'; printf ' <%s>' "$@"; printf '\n'; }
                prepare_signing_keychain
                version=1 build_version=1 release_artifacts=unused source_seal=unused
                campaign_dll=unused build_artifacts=unused
                """ + "\n" + package + "\n/usr/bin/env");
            start.ArgumentList.Add("handoff-test");
            start.ArgumentList.Add(directory);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            Assert.Equal("synthetic certificate", File.ReadAllText(Path.Combine(directory, "certificate.p12")));
            Assert.Equal("synthetic notary key", File.ReadAllText(Path.Combine(directory, "AuthKey.p8")));
            Assert.Contains("<-P> <synthetic password>", output, StringComparison.Ordinal);
            Assert.Contains("<--key-id> <synthetic key id> <--issuer> <synthetic issuer>", output, StringComparison.Ordinal);
            Assert.Contains("<--sign-identity> <synthetic identity> <--notary-profile> <synthetic profile>", output, StringComparison.Ordinal);
            Assert.Contains("<--keychain> <" + directory + "/test.keychain>", output, StringComparison.Ordinal);
            Assert.DoesNotContain("APPLE_", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Startup_error_UI_is_only_started_by_synchronous_Main()
    {
        var program = Read("src/Asura.Desktop/Program.cs");
        var preparation = program.IndexOf("private static async Task<StartupPreparation?> PrepareAsync(DesktopProfileConfiguration profile)", StringComparison.Ordinal);
        Assert.True(preparation > 0);
        Assert.DoesNotContain("DesktopStartupFailurePresenter.TryShow", program[preparation..], StringComparison.Ordinal);
        var main = program[..preparation];
        Assert.Contains("var prepared = PrepareAsync(profile).GetAwaiter().GetResult();", main, StringComparison.Ordinal);
        Assert.Contains("prepared is StartupPreparation.Failed failure", main, StringComparison.Ordinal);
        Assert.Contains("DesktopStartupFailurePresenter.TryShow", main, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
            }
        }
        throw new DirectoryNotFoundException("Unable to locate the Asura repository root.");
    }
}

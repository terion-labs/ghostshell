using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asura.Packaging;

namespace Asura.AccessibilityAcceptance;

public sealed class NativeTerminalPackageProvenanceTests : IDisposable
{
    private const string Commit =
        "08f039fbb3dea9c6b1cdb5ff4550666598122346";

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-native-terminal-tests-{Guid.NewGuid():N}");

    public NativeTerminalPackageProvenanceTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Libghostty_vt_receipt_validates_its_exact_package_payload()
    {
        var fixture = CreateFixture();

        Assert.True(NativeTerminalPackageProvenance.IsCatalog(fixture.CatalogPath));
        NativeTerminalPackageProvenance.Validate(
            fixture.ExecutableDirectory,
            fixture.ExecutableDirectory,
            fixture.ExecutableDirectory,
            fixture.LicenseDirectory,
            fixture.CatalogPath,
            fixture.ReceiptPath);
    }

    [Fact]
    public void Libghostty_vt_receipt_rejects_a_changed_library()
    {
        var fixture = CreateFixture();
        File.AppendAllText(
            Path.Combine(fixture.ExecutableDirectory, "libghostty-vt.dylib"),
            "changed");

        Assert.Throws<InvalidDataException>(() =>
            NativeTerminalPackageProvenance.Validate(
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Fact]
    public void Macos_codesign_transformation_preserves_post_sign_provenance_contract()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var fixture = CreateFixture();
        var libraryPath = Path.Combine(
            fixture.ExecutableDirectory,
            "libghostty-vt.dylib");
        File.Copy("/usr/bin/true", libraryPath, overwrite: true);
        UpdateLibraryEvidence(fixture, libraryPath);
        NativeTerminalPackageProvenance.Validate(
            fixture.ExecutableDirectory,
            fixture.ExecutableDirectory,
            fixture.ExecutableDirectory,
            fixture.LicenseDirectory,
            fixture.CatalogPath,
            fixture.ReceiptPath);
        var unsignedSha256 = Sha256(libraryPath);

        var start = new ProcessStartInfo("/usr/bin/codesign")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "--force", "--sign", "-", libraryPath })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.NotEqual(
            unsignedSha256,
            Sha256(libraryPath),
            StringComparer.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            NativeTerminalPackageProvenance.Validate(
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));

        NativeTerminalPackageProvenance.ValidateAfterCodeSigning(
            fixture.ExecutableDirectory,
            fixture.ExecutableDirectory,
            fixture.ExecutableDirectory,
            fixture.LicenseDirectory,
            fixture.CatalogPath,
            fixture.ReceiptPath);
    }

    [Fact]
    public void Missing_signature_independent_identity_is_rejected_before_signing()
    {
        var fixture = CreateFixture();
        var receipt = JsonNode.Parse(File.ReadAllText(fixture.ReceiptPath))!.AsObject();
        receipt["artifact"]!.AsObject().Remove("signatureRemovedSha256");
        File.WriteAllText(fixture.ReceiptPath, receipt.ToJsonString());
        File.Copy(fixture.ReceiptPath,
            Path.Combine(fixture.LicenseDirectory, "Native", "native-terminal-build-receipt.json"),
            overwrite: true);

        Assert.Throws<InvalidDataException>(() => NativeTerminalPackageProvenance.Validate(
            fixture.ExecutableDirectory,
            fixture.ExecutableDirectory,
            fixture.ExecutableDirectory,
            fixture.LicenseDirectory,
            fixture.CatalogPath,
            fixture.ReceiptPath));
    }

    [Fact]
    public void Signed_package_metadata_rejects_an_invalid_build_digest()
    {
        var fixture = CreateFixture();
        var receipt = JsonNode.Parse(File.ReadAllText(fixture.ReceiptPath))!.AsObject();
        receipt["artifact"]!["sha256"] = "not-a-sha256";
        File.WriteAllText(fixture.ReceiptPath, receipt.ToJsonString());
        File.Copy(
            fixture.ReceiptPath,
            Path.Combine(
                fixture.LicenseDirectory,
                "Native",
                "native-terminal-build-receipt.json"),
            overwrite: true);

        Assert.Throws<InvalidDataException>(() =>
            NativeTerminalPackageProvenance.ValidateAfterCodeSigning(
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Fact]
    public void Post_sign_validation_rejects_a_different_valid_MachO_program()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var fixture = CreateFixture();
        var libraryPath = Path.Combine(fixture.ExecutableDirectory, "libghostty-vt.dylib");
        File.Copy("/usr/bin/true", libraryPath, overwrite: true);
        UpdateLibraryEvidence(fixture, libraryPath);
        File.Copy("/usr/bin/false", libraryPath, overwrite: true);

        Assert.Throws<InvalidDataException>(() =>
            NativeTerminalPackageProvenance.ValidateAfterCodeSigning(
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Fact]
    public void Signature_normalization_is_stable_across_repeated_ad_hoc_signing()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var libraryPath = Path.Combine(_temporaryDirectory, "native-library");
        File.Copy("/usr/bin/true", libraryPath);
        var expected = NativeTerminalPackageProvenance.ComputeSignatureRemovedSha256(libraryPath);
        foreach (var identifier in new[] { "sh.asura.fixture.one", "sh.asura.fixture.two" })
        {
            var start = new ProcessStartInfo("/usr/bin/codesign")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "--force", "--sign", "-", "--identifier", identifier, libraryPath })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            var signedSha = Sha256(libraryPath);
            Assert.Equal(expected, NativeTerminalPackageProvenance.ComputeSignatureRemovedSha256(libraryPath));
            Assert.Equal(signedSha, Sha256(libraryPath));
        }
    }

    private static void UpdateLibraryEvidence(Fixture fixture, string libraryPath)
    {
        var receipt = JsonNode.Parse(File.ReadAllText(fixture.ReceiptPath))!.AsObject();
        receipt["artifact"]!["bytes"] = new FileInfo(libraryPath).Length;
        receipt["artifact"]!["sha256"] = Sha256(libraryPath);
        receipt["artifact"]!["signatureRemovedSha256"] =
            NativeTerminalPackageProvenance.ComputeSignatureRemovedSha256(libraryPath);
        File.WriteAllText(fixture.ReceiptPath, receipt.ToJsonString());
        File.Copy(
            fixture.ReceiptPath,
            Path.Combine(
                fixture.LicenseDirectory,
                "Native",
                "native-terminal-build-receipt.json"),
            overwrite: true);
    }

    [Fact]
    public void Libghostty_vt_receipt_rejects_a_changed_shell_integration_script()
    {
        var fixture = CreateFixture();
        File.AppendAllText(
            Path.Combine(
                fixture.ExecutableDirectory,
                "ghostty",
                "shell-integration",
                "zsh",
                "ghostty-integration"),
            "changed");

        Assert.Throws<InvalidDataException>(() =>
            NativeTerminalPackageProvenance.Validate(
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Fact]
    public void Libghostty_vt_receipt_rejects_a_changed_export_manifest()
    {
        var fixture = CreateFixture();
        File.AppendAllText(
            Path.Combine(
                fixture.ExecutableDirectory,
                "ghostty-vt-required-exports.txt"),
            "ghostty_unreviewed_export\n");

        Assert.Throws<InvalidDataException>(() =>
            NativeTerminalPackageProvenance.Validate(
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Theory]
    [InlineData("asuraExtension", 2)]
    [InlineData("testsPassed", false)]
    public void Libghostty_vt_receipt_rejects_unverified_native_evidence(
        string property,
        object value)
    {
        var fixture = CreateFixture();
        var receipt = JsonNode.Parse(File.ReadAllText(fixture.ReceiptPath))!.AsObject();
        if (string.Equals(property, "testsPassed", StringComparison.Ordinal))
        {
            receipt["build"]![property] = (bool)value;
        }
        else
        {
            receipt["abi"]![property] = (int)value;
        }

        File.WriteAllText(fixture.ReceiptPath, receipt.ToJsonString());
        File.Copy(
            fixture.ReceiptPath,
            Path.Combine(
                fixture.LicenseDirectory,
                "Native",
                "native-terminal-build-receipt.json"),
            overwrite: true);

        Assert.Throws<InvalidDataException>(() =>
            NativeTerminalPackageProvenance.Validate(
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.ExecutableDirectory,
                fixture.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));
    }

    [Fact]
    public void Descriptor_safe_pty_bytes_are_receipt_bound()
    {
        var fixture = CreateFixture();
        File.AppendAllText(Path.Combine(fixture.ExecutableDirectory, "libasura_pty.dylib"), "changed");
        Assert.Throws<InvalidDataException>(() => NativeTerminalPackageProvenance.Validate(
            fixture.ExecutableDirectory, fixture.ExecutableDirectory, fixture.ExecutableDirectory,
            fixture.LicenseDirectory, fixture.CatalogPath, fixture.ReceiptPath));
    }

    [Fact]
    public void Old_receipt_without_descriptor_boundary_is_rejected()
    {
        var fixture = CreateFixture();
        var receipt = JsonNode.Parse(File.ReadAllText(fixture.ReceiptPath))!.AsObject();
        receipt.Remove("pty");
        File.WriteAllText(fixture.ReceiptPath, receipt.ToJsonString());
        File.Copy(fixture.ReceiptPath, Path.Combine(fixture.LicenseDirectory, "Native", "native-terminal-build-receipt.json"), true);
        Assert.Throws<InvalidDataException>(() => NativeTerminalPackageProvenance.Validate(
            fixture.ExecutableDirectory, fixture.ExecutableDirectory, fixture.ExecutableDirectory,
            fixture.LicenseDirectory, fixture.CatalogPath, fixture.ReceiptPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private Fixture CreateFixture()
    {
        var executableDirectory = Path.Combine(_temporaryDirectory, "MacOS");
        var licenseDirectory = Path.Combine(_temporaryDirectory, "Licenses");
        var nativeLicenseDirectory = Path.Combine(licenseDirectory, "Native");
        Directory.CreateDirectory(executableDirectory);
        Directory.CreateDirectory(nativeLicenseDirectory);

        var catalogPath = Path.Combine(_temporaryDirectory, "catalog.json");
        var catalog = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            format = "asura-native-terminal-component-catalog-v1",
            pty = PtyTestProvenance.Catalog,
            component = new
            {
                sourceCommit = Commit,
            },
        });
        File.WriteAllBytes(catalogPath, catalog);

        var libraryPath = Path.Combine(
            executableDirectory,
            "libghostty-vt.dylib");
        var requiredExportsPath = Path.Combine(
            executableDirectory,
            "ghostty-vt-required-exports.txt");
        var licensePath = Path.Combine(licenseDirectory, "GHOSTTY-LICENSE");
        File.WriteAllBytes(libraryPath, [1, 2, 3, 4]);
        File.WriteAllLines(
            requiredExportsPath,
            [
                "ghostty_asura_extension_abi",
                "ghostty_build_info",
                "ghostty_terminal_new",
            ]);
        File.WriteAllText(licensePath, "Ghostty MIT license fixture");
        var shellIntegrationDirectory = Path.Combine(
            executableDirectory,
            "ghostty",
            "shell-integration");
        var shellIntegrationFiles = new[]
        {
            "SHELL-INTEGRATION-NOTICE.md",
            "bash/bash-preexec.sh",
            "bash/ghostty.bash",
            "fish/vendor_conf.d/ghostty-shell-integration.fish",
            "zsh/.zshenv",
            "zsh/ghostty-integration",
        };
        Directory.CreateDirectory(shellIntegrationDirectory);
        var manifestLines = new List<string>();
        foreach (var relativePath in shellIntegrationFiles)
        {
            var path = Path.Combine(
                shellIntegrationDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"fixture {relativePath}");
            manifestLines.Add($"{Sha256(path)}  {relativePath}");
        }

        var manifestPath = Path.Combine(
            shellIntegrationDirectory,
            "MANIFEST.sha256");
        File.WriteAllLines(manifestPath, manifestLines);

        var receiptPath = Path.Combine(_temporaryDirectory, "receipt.json");
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            format = "asura-native-terminal-build-receipt-v1",
            pty = PtyTestProvenance.Create(executableDirectory, licenseDirectory),
            catalogSha256 = Sha256(catalogPath),
            targetRid = "osx-arm64",
            source = new
            {
                commit = Commit,
            },
            build = new
            {
                testsTargetRid = "osx-arm64",
                testsPassed = true,
            },
            abi = new
            {
                asuraExtension = 1,
                asuraExtensionExport = "ghostty_asura_extension_abi",
                requiredExportsPath = "ghostty-vt-required-exports.txt",
                requiredExportsCount = 3,
                requiredExportsBytes = new FileInfo(requiredExportsPath).Length,
                requiredExportsSha256 = Sha256(requiredExportsPath),
            },
            artifact = new
            {
                path = "libghostty-vt.dylib",
                bytes = new FileInfo(libraryPath).Length,
                sha256 = Sha256(libraryPath),
                signatureRemovedSha256 = new string('0', 64),
            },
            license = new
            {
                path = "GHOSTTY-LICENSE",
                bytes = new FileInfo(licensePath).Length,
                sha256 = Sha256(licensePath),
            },
            shellIntegration = new
            {
                directory = "ghostty/shell-integration",
                manifestPath = "ghostty/shell-integration/MANIFEST.sha256",
                manifestBytes = new FileInfo(manifestPath).Length,
                manifestSha256 = Sha256(manifestPath),
                fileCount = shellIntegrationFiles.Length,
            },
        });
        File.WriteAllBytes(receiptPath, receipt);

        File.Copy(
            catalogPath,
            Path.Combine(nativeLicenseDirectory, "native-terminal-components.json"));
        File.Copy(
            receiptPath,
            Path.Combine(nativeLicenseDirectory, "native-terminal-build-receipt.json"));

        return new Fixture(
            executableDirectory,
            licenseDirectory,
            catalogPath,
            receiptPath);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed record Fixture(
        string ExecutableDirectory,
        string LicenseDirectory,
        string CatalogPath,
        string ReceiptPath);
}

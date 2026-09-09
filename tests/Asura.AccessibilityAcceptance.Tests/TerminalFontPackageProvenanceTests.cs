using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Asura.Packaging;

namespace Asura.AccessibilityAcceptance;

public sealed class TerminalFontPackageProvenanceTests : IDisposable
{
    private const string SourceCommit =
        "08f039fbb3dea9c6b1cdb5ff4550666598122346";
    private const string ZigPackageHash =
        "N-V-__8AAIC5lwAVPJJzxnCAahSvZTIlG-HhtOvnM1uh-66x";

    private static readonly Asset[] Assets =
    [
        new(
            "JetBrainsMono-Regular.ttf",
            "fonts/ttf/JetBrainsMono-Regular.ttf",
            "normal",
            400,
            273900,
            "a0bf60ef0f83c5ed4d7a75d45838548b1f6873372dfac88f71804491898d138f"),
        new(
            "JetBrainsMono-Bold.ttf",
            "fonts/ttf/JetBrainsMono-Bold.ttf",
            "normal",
            700,
            277828,
            "5590990c82e097397517f275f430af4546e1c45cff408bde4255dad142479dcb"),
        new(
            "JetBrainsMono-Italic.ttf",
            "fonts/ttf/JetBrainsMono-Italic.ttf",
            "italic",
            400,
            276840,
            "9d0a1f7a708e6af183f1193b7e81d40da294f5c67682c085d8401c60aac8ded4"),
        new(
            "JetBrainsMono-BoldItalic.ttf",
            "fonts/ttf/JetBrainsMono-BoldItalic.ttf",
            "italic",
            700,
            279832,
            "4039d5ce0ed225bf9c8b2c8c6436290ae2f356b7e90d70fa666227238324aa3b"),
    ];

    private static readonly Asset License = new(
        "OFL.txt",
        "OFL.txt",
        "license",
        0,
        4399,
        "30f0c136e3c88e422d0791acd97238870f9054a9729bc34cf2ff0d4ed8cac4ad");

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-terminal-font-tests-{Guid.NewGuid():N}");

    public TerminalFontPackageProvenanceTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Pinned_jetbrains_mono_receipt_validates_its_exact_package_payload()
    {
        var fixture = CreateFixture();

        TerminalFontPackageProvenance.Validate(
            fixture.ExecutableDirectory,
            fixture.LicenseDirectory,
            fixture.CatalogPath,
            fixture.ReceiptPath);
    }

    [Fact]
    public void Receipt_rejects_a_changed_font_face()
    {
        var fixture = CreateFixture();
        File.AppendAllText(
            Path.Combine(fixture.FontDirectory, "JetBrainsMono-Regular.ttf"),
            "changed");

        AssertRejected(fixture);
    }

    [Fact]
    public void Receipt_rejects_a_changed_packaged_ofl_license()
    {
        var fixture = CreateFixture();
        File.AppendAllText(Path.Combine(fixture.FontDirectory, "OFL.txt"), "changed");

        AssertRejected(fixture);
    }

    [Fact]
    public void Receipt_rejects_a_changed_installed_ofl_license()
    {
        var fixture = CreateFixture();
        File.AppendAllText(
            Path.Combine(fixture.LicenseDirectory, "JetBrainsMono-OFL.txt"),
            "changed");

        AssertRejected(fixture);
    }

    [Fact]
    public void Receipt_rejects_a_catalog_with_a_different_digest()
    {
        var fixture = CreateFixture();
        File.AppendAllText(fixture.CatalogPath, "\n");
        File.Copy(
            fixture.CatalogPath,
            Path.Combine(
                fixture.LicenseDirectory,
                "Native",
                "terminal-font-assets.json"),
            overwrite: true);

        AssertRejected(fixture);
    }

    [Fact]
    public void Receipt_rejects_a_packaged_receipt_that_is_not_the_reviewed_copy()
    {
        var fixture = CreateFixture();
        File.AppendAllText(
            Path.Combine(
                fixture.LicenseDirectory,
                "Native",
                "terminal-font-assets-build-receipt.json"),
            "\n");

        AssertRejected(fixture);
    }

    [Fact]
    public void Receipt_rejects_an_unexpected_file_in_the_font_closure()
    {
        var fixture = CreateFixture();
        File.WriteAllText(Path.Combine(fixture.FontDirectory, "unreviewed.txt"), "no");

        AssertRejected(fixture);
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
        var fontDirectory = Path.Combine(
            executableDirectory,
            "fonts",
            "JetBrainsMono");
        var licenseDirectory = Path.Combine(_temporaryDirectory, "Licenses");
        var nativeLicenseDirectory = Path.Combine(licenseDirectory, "Native");
        Directory.CreateDirectory(fontDirectory);
        Directory.CreateDirectory(nativeLicenseDirectory);

        var source = FindPinnedFontSource();
        foreach (var asset in Assets)
        {
            File.Copy(
                Path.Combine(source.FaceDirectory, asset.File),
                Path.Combine(fontDirectory, asset.File));
        }
        File.Copy(source.LicensePath, Path.Combine(fontDirectory, License.File));

        var manifestPath = Path.Combine(fontDirectory, "MANIFEST.sha256");
        var manifestLines = Assets
            .Append(License)
            .OrderBy(static asset => asset.File, StringComparer.Ordinal)
            .Select(static asset => $"{asset.Sha256}  {asset.File}");
        File.WriteAllText(
            manifestPath,
            string.Join('\n', manifestLines) + "\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Copy(
            Path.Combine(fontDirectory, "OFL.txt"),
            Path.Combine(licenseDirectory, "JetBrainsMono-OFL.txt"));

        var catalogPath = Path.Combine(_temporaryDirectory, "catalog.json");
        var catalog = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            format = "asura-terminal-font-assets-catalog-v1",
            catalogId = "asura-jetbrains-mono-2.304-20260801",
            source = new
            {
                repository = "https://github.com/ghostty-org/ghostty.git",
                commit = SourceCommit,
            },
            dependency = DependencyEvidence(),
            assets = Assets.Select(static asset => new
            {
                file = asset.File,
                sourcePath = asset.SourcePath,
                style = asset.Style,
                weight = asset.Weight,
                bytes = asset.Bytes,
                sha256 = asset.Sha256,
            }),
            license = new
            {
                file = License.File,
                sourcePath = License.SourcePath,
                bytes = License.Bytes,
                sha256 = License.Sha256,
            },
        });
        File.WriteAllBytes(catalogPath, catalog);

        var receiptPath = Path.Combine(_temporaryDirectory, "receipt.json");
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            format = "asura-terminal-font-assets-build-receipt-v1",
            generator = "scripts/build-terminal-font-assets.sh",
            catalogSha256 = Sha256(catalogPath),
            source = new
            {
                repository = "https://github.com/ghostty-org/ghostty.git",
                commit = SourceCommit,
            },
            dependency = DependencyEvidence(),
            directory = "fonts/JetBrainsMono",
            manifest = new
            {
                path = "fonts/JetBrainsMono/MANIFEST.sha256",
                fileCount = Assets.Length + 1,
                bytes = new FileInfo(manifestPath).Length,
                sha256 = Sha256(manifestPath),
            },
            assets = Assets.Select(static asset => new
            {
                file = asset.File,
                sourcePath = asset.SourcePath,
                style = asset.Style,
                weight = asset.Weight,
                bytes = asset.Bytes,
                sha256 = asset.Sha256,
            }),
            license = new
            {
                path = "fonts/JetBrainsMono/OFL.txt",
                bytes = License.Bytes,
                sha256 = License.Sha256,
            },
        });
        File.WriteAllBytes(receiptPath, receipt);

        File.Copy(
            catalogPath,
            Path.Combine(nativeLicenseDirectory, "terminal-font-assets.json"));
        File.Copy(
            receiptPath,
            Path.Combine(
                nativeLicenseDirectory,
                "terminal-font-assets-build-receipt.json"));

        return new Fixture(
            executableDirectory,
            fontDirectory,
            licenseDirectory,
            catalogPath,
            receiptPath);
    }

    private static object DependencyEvidence() => new
    {
        name = "JetBrains Mono",
        version = "2.304",
        url = "https://deps.files.ghostty.org/JetBrainsMono-2.304.tar.gz",
        zigPackageHash = ZigPackageHash,
        license = "OFL-1.1",
    };

    private static PinnedFontSource FindPinnedFontSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var commonArtifact = Path.Combine(
            repositoryRoot,
            "native",
            "artifacts",
            "common",
            "fonts",
            "JetBrainsMono");
        if (Assets.Append(License).All(asset =>
                File.Exists(Path.Combine(commonArtifact, asset.File))))
        {
            return new PinnedFontSource(
                commonArtifact,
                Path.Combine(commonArtifact, License.File));
        }

        var zigPackage = Path.Combine(
            repositoryRoot,
            ".deps",
            "ghostty-vt",
            "zig-pkg",
            ZigPackageHash);
        var sourceDirectory = Path.Combine(zigPackage, "fonts", "ttf");
        if (Assets.All(asset =>
                File.Exists(Path.Combine(sourceDirectory, asset.File)))
            && File.Exists(Path.Combine(zigPackage, "OFL.txt")))
        {
            return new PinnedFontSource(
                sourceDirectory,
                Path.Combine(zigPackage, "OFL.txt"));
        }

        throw new InvalidOperationException(
            "Pinned JetBrains Mono assets are unavailable. Run scripts/bootstrap-native-terminal.sh first.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The Asura repository root was not found.");
    }

    private static void AssertRejected(Fixture fixture) =>
        Assert.Throws<InvalidDataException>(() =>
            TerminalFontPackageProvenance.Validate(
                fixture.ExecutableDirectory,
                fixture.LicenseDirectory,
                fixture.CatalogPath,
                fixture.ReceiptPath));

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed record Asset(
        string File,
        string SourcePath,
        string Style,
        int Weight,
        long Bytes,
        string Sha256);

    private sealed record Fixture(
        string ExecutableDirectory,
        string FontDirectory,
        string LicenseDirectory,
        string CatalogPath,
        string ReceiptPath);

    private sealed record PinnedFontSource(
        string FaceDirectory,
        string LicensePath);
}

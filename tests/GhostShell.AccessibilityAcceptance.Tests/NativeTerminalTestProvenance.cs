using System.Security.Cryptography;
using System.Text.Json;

namespace GhostShell.AccessibilityAcceptance;

internal sealed record NativeTerminalTestEvidence(
    string CatalogPath,
    string ReceiptPath,
    string FontCatalogPath,
    string FontReceiptPath);

internal static class NativeTerminalTestProvenance
{
    private const string Commit =
        "08f039fbb3dea9c6b1cdb5ff4550666598122346";

    private static readonly string[] ShellIntegrationFiles =
    [
        "SHELL-INTEGRATION-NOTICE.md",
        "bash/bash-preexec.sh",
        "bash/ghostty.bash",
        "fish/vendor_conf.d/ghostty-shell-integration.fish",
        "zsh/.zshenv",
        "zsh/ghostty-integration",
    ];

    public static NativeTerminalTestEvidence AddToPublish(
        string publishDirectory,
        string fixtureDirectory)
    {
        var libraryPath = Path.Combine(publishDirectory, "libghostty-vt.dylib");
        File.WriteAllBytes(libraryPath, [1, 2, 3, 4]);
        var requiredExportsPath = Path.Combine(
            publishDirectory,
            "ghostty-vt-required-exports.txt");
        File.WriteAllLines(
            requiredExportsPath,
            [
                "ghostty_build_info",
                "ghostty_ghostshell_extension_abi",
                "ghostty_terminal_new",
            ]);

        var shellDirectory = Path.Combine(
            publishDirectory,
            "ghostty",
            "shell-integration");
        Directory.CreateDirectory(shellDirectory);
        var manifestLines = new List<string>();
        foreach (var relativePath in ShellIntegrationFiles)
        {
            var path = Path.Combine(
                shellDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"fixture {relativePath}");
            manifestLines.Add($"{Sha256(path)}  {relativePath}");
        }

        var manifestPath = Path.Combine(shellDirectory, "MANIFEST.sha256");
        File.WriteAllLines(manifestPath, manifestLines);

        var catalogPath = Path.Combine(fixtureDirectory, "native-terminal-components.json");
        var catalog = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            format = "ghostshell-native-terminal-component-catalog-v1",
            pty = PtyTestProvenance.Catalog,
            component = new
            {
                identity = "libghostty-vt/0.1.0-dev",
                sourceCommit = Commit,
            },
        });
        File.WriteAllBytes(catalogPath, catalog);

        var licensePath = Path.Combine(publishDirectory, "GHOSTTY-LICENSE");
        var receiptPath = Path.Combine(fixtureDirectory, "native-terminal-build-receipt.json");
        var receipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            format = "ghostshell-native-terminal-build-receipt-v1",
            pty = PtyTestProvenance.Create(publishDirectory, publishDirectory),
            catalogSha256 = Sha256(catalogPath),
            targetRid = "osx-arm64",
            source = new { commit = Commit },
            build = new
            {
                testsTargetRid = "osx-arm64",
                testsPassed = true,
            },
            abi = new
            {
                ghostShellExtension = 1,
                ghostShellExtensionExport = "ghostty_ghostshell_extension_abi",
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
                fileCount = ShellIntegrationFiles.Length,
            },
        });
        File.WriteAllBytes(receiptPath, receipt);

        File.Copy(
            catalogPath,
            Path.Combine(publishDirectory, "native-terminal-components.json"));
        File.Copy(
            receiptPath,
            Path.Combine(publishDirectory, "native-terminal-build-receipt.json"));

        var repositoryRoot = FindRepositoryRoot();
        var commonArtifact = Path.Combine(
            repositoryRoot,
            "native",
            "artifacts",
            "common");
        var commonFontDirectory = Path.Combine(
            commonArtifact,
            "fonts",
            "JetBrainsMono");
        var fontCatalogSource = Path.Combine(
            repositoryRoot,
            "licenses",
            "terminal-font-assets.json");
        var fontReceiptSource = Path.Combine(
            commonArtifact,
            "terminal-font-assets-build-receipt.json");
        var fontFiles = new[]
        {
            "JetBrainsMono-Bold.ttf",
            "JetBrainsMono-BoldItalic.ttf",
            "JetBrainsMono-Italic.ttf",
            "JetBrainsMono-Regular.ttf",
            "MANIFEST.sha256",
            "OFL.txt",
        };
        if (!File.Exists(fontCatalogSource)
            || !File.Exists(fontReceiptSource)
            || fontFiles.Any(file => !File.Exists(
                Path.Combine(commonFontDirectory, file))))
        {
            throw new InvalidOperationException(
                "Pinned terminal font assets are unavailable. Run "
                + "scripts/build-terminal-font-assets.sh first.");
        }

        var publishFontDirectory = Path.Combine(
            publishDirectory,
            "fonts",
            "JetBrainsMono");
        Directory.CreateDirectory(publishFontDirectory);
        foreach (var file in fontFiles)
        {
            File.Copy(
                Path.Combine(commonFontDirectory, file),
                Path.Combine(publishFontDirectory, file));
        }

        var fontCatalogPath = Path.Combine(
            fixtureDirectory,
            "terminal-font-assets.json");
        var fontReceiptPath = Path.Combine(
            fixtureDirectory,
            "terminal-font-assets-build-receipt.json");
        File.Copy(fontCatalogSource, fontCatalogPath);
        File.Copy(fontReceiptSource, fontReceiptPath);
        File.Copy(
            fontCatalogPath,
            Path.Combine(publishDirectory, "terminal-font-assets.json"));
        File.Copy(
            fontReceiptPath,
            Path.Combine(
                publishDirectory,
                "terminal-font-assets-build-receipt.json"));
        File.Copy(
            Path.Combine(commonFontDirectory, "OFL.txt"),
            Path.Combine(publishDirectory, "JETBRAINS-MONO-OFL.txt"));

        return new NativeTerminalTestEvidence(
            catalogPath,
            receiptPath,
            fontCatalogPath,
            fontReceiptPath);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
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
            "The GhostSHELL repository root could not be located.");
    }
}

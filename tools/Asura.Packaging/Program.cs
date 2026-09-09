namespace Asura.Packaging;

internal static class Program
{
    private const int FailedExitCode = 1;
    private const int UsageExitCode = 64;

    public static int Main(string[] args)
    {
        try
        {
            return args.FirstOrDefault()?.ToLowerInvariant() switch
            {
                "macos" => BuildMacOs(MacOsPackagingCommand.Parse(args[1..])),
                "workspace-backend-evidence" => WriteBackendEvidence(args[1..]),
                "native-signature-removed-sha256" => PrintNativeContentDigest(args[1..]),
                "macos-release-legal" => ValidateMacOsReleaseLegal(args[1..]),
                "cef-runtime-receipt" => CreateCefRuntimeReceipt(
                    CefRuntimeReceiptCommand.Parse(args[1..])),
                "cef-runtime-validate" => ValidateCefRuntime(
                    CefRuntimeValidateCommand.Parse(args[1..])),
                "velopack-macos-validate" => ValidateVelopackMacOsRelease(
                    VelopackMacOsReleaseCommand.Parse(args[1..])),
                "native-publish-artifacts" =>
                    PublishNativeArtifacts(
                        NativeArtifactPublishCommand.Parse(args[1..])),
                "--help" or "-h" or "help" => PrintHelpAndReturn(),
                _ => throw new PackagingUsageException(
                    "Expected a supported packaging command."),
            };
        }
        catch (PackagingUsageException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return UsageExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Asura packaging failed: {exception.Message}");
            return FailedExitCode;
        }
    }

    private static int WriteBackendEvidence(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 5 || arguments.Any(string.IsNullOrWhiteSpace))
        {
            throw new PackagingUsageException("workspace-backend-evidence requires publish directory, license directory, catalog path, NuGet root, and product version.");
        }
        var profile = Environment.GetEnvironmentVariable("ASURA_BACKEND_ARCH") is "x64"
            ? ManagedEvidenceProfile.LinuxX64Backend : ManagedEvidenceProfile.LinuxBackend;
        var result = ManagedComponentEvidenceBuilder.Build(arguments[0], arguments[1], arguments[2], arguments[3], arguments[4],
            new(1024, 100000, 64L * 1024 * 1024, 16), profile);
        // The caller chooses the parent location, but payload-controlled links
        // must never redirect generated legal evidence outside that payload.
        RequireUnlinkedEvidenceDirectory(arguments[0]);
        var legalDirectory = Path.Combine(arguments[0], "legal");
        RequireUnlinkedEvidenceDirectory(legalDirectory);
        var destination = Path.Combine(legalDirectory, "managed");
        RequireUnlinkedEvidenceDirectory(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new InvalidDataException("The backend evidence output must not already exist.");
        }
        Directory.CreateDirectory(destination);
        foreach (var file in result.Files)
        {
            var path = Path.Combine(destination, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(file.Content);
        }
        Console.WriteLine("Verified exact Linux backend managed package closure and generated SPDX/license evidence.");
        return 0;
    }

    private static void RequireUnlinkedEvidenceDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null
            || (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            || File.Exists(path))
        {
            throw new InvalidDataException("Backend evidence directories must be ordinary, unlinked directories.");
        }
    }

    private static int PublishNativeArtifacts(
        NativeArtifactPublishCommand command)
    {
        var result = NativeArtifactPublisher.Publish(
            command.StagedDirectory,
            command.DestinationDirectory,
            command.Component);
        Console.WriteLine(
            result.ReplacedExistingDirectory
                ? "Atomically replaced the native artifact directory."
                : "Published the native artifact directory.");
        return 0;
    }

    private static int PrintNativeContentDigest(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1 || string.IsNullOrWhiteSpace(arguments[0]))
        {
            throw new PackagingUsageException("native-signature-removed-sha256 requires one Mach-O path.");
        }
        Console.WriteLine(NativeTerminalPackageProvenance.ComputeSignatureRemovedSha256(arguments[0]));
        return 0;
    }

    private static int BuildMacOs(MacOsPackagingCommand command)
    {
        var result = new MacOsAppBundleBuilder().Build(command.ToRequest());
        Console.WriteLine(
            $"Created Asura.app {result.ProductVersion} "
            + $"({result.FileCount} files, build {result.BuildVersion}).");
        return 0;
    }

    private static int ValidateMacOsReleaseLegal(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 5
            || !string.Equals(arguments[0], "--record", StringComparison.Ordinal)
            || !string.Equals(arguments[2], "--source-root", StringComparison.Ordinal)
            || !string.Equals(arguments[4], "--require-clearance", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[1])
            || string.IsNullOrWhiteSpace(arguments[3]))
        {
            throw new PackagingUsageException(
                "macos-release-legal requires --record <path>, "
                + "--source-root <directory>, and --require-clearance.");
        }

        var inspection = MacOsReleaseLegalClosure.Validate(
            arguments[1],
            arguments[3]);
        MacOsReleaseLegalClosure.RequirePublicationClearance(inspection);
        Console.WriteLine("Validated macOS release legal clearance.");
        return 0;
    }

    private static int CreateCefRuntimeReceipt(CefRuntimeReceiptCommand command)
    {
        CefRuntimeReceipt.Create(
            command.RuntimeRoot,
            command.CatalogPath,
            command.RuntimeIdentifier,
            command.ArchiveSha1,
            command.ArchiveSha256,
            command.PatchSetSha256,
            command.SourceSnapshotSha256,
            command.OutputPath);
        Console.WriteLine(
            $"Created verified CEF runtime receipt for {command.RuntimeIdentifier}.");
        return 0;
    }

    private static int ValidateCefRuntime(CefRuntimeValidateCommand command)
    {
        var inspection = CefRuntimeReceipt.Validate(
            command.RuntimeRoot,
            command.CatalogPath,
            command.RuntimeIdentifier);
        Console.WriteLine(
            $"Validated CEF runtime {inspection.Catalog.CefVersion} for "
            + $"{inspection.Rid} ({inspection.Files.Count} files).");
        return 0;
    }

    private static int ValidateVelopackMacOsRelease(
        VelopackMacOsReleaseCommand command)
    {
        var inspection = VelopackMacOsRelease.Validate(command);
        Console.WriteLine(
            $"Validated {inspection.PackageFileName} against "
            + $"{inspection.ApplicationFileCount} application files "
            + $"({inspection.PackageSha256}).");
        return 0;
    }

    private static int PrintHelpAndReturn()
    {
        PrintHelp();
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Asura packaging

              native-signature-removed-sha256 <Mach-O-file>

              workspace-backend-evidence <publish-directory> <license-directory>
                    <component-catalog> <nuget-packages-directory> <product-version>

              macos --publish <native-aot-directory>
                    --managed-evidence <self-contained-directory>
                    --output <Asura.app>
                    --version <major.minor.patch> --build-version <number[.number...]>
                    --product-identity-manifest <product-identity.json>
                    --product-identity-source-root <repository-directory>
                    --asset-catalog <Xcode-output/Assets.car>
                    --component-catalog <managed-components.json>
                    --native-component-catalog <native-terminal-components.json>
                    --native-build-receipt <native-terminal-build-receipt.json>
                    --font-assets-catalog <terminal-font-assets.json>
                    --font-assets-build-receipt <terminal-font-assets-build-receipt.json>
                    --nuget-packages <global-packages-directory>
                    --cef-runtime-root <verified-runtime-directory>
                    --cef-runtime-catalog <cef-runtime-components.json>
                    --runtime-identifier <osx-arm64>

              macos-release-legal --record <macos-release-legal.json>
                    --source-root <repository-directory> --require-clearance

              cef-runtime-receipt --runtime-root <staged-directory>
                    --catalog <cef-runtime-components.json>
                    --runtime-identifier <rid> --archive-sha1 <hex>
                    --archive-sha256 <hex>
                    --patch-set-sha256 <hex>
                    --source-snapshot-sha256 <hex>
                    --output <staged-directory/cef-runtime-build-receipt.json>

              cef-runtime-validate --runtime-root <staged-directory>
                    --catalog <cef-runtime-components.json>
                    --runtime-identifier <rid>

              velopack-macos-validate --release-directory <directory>
                    --full-package <sh.asura-...-full.nupkg>
                    --app <extracted/Asura.app>
                    --version <major.minor.patch>
                    --channel <osx-arm64-track>

              native-publish-artifacts --staged-directory <directory>
                    --destination <native/artifacts/runtime-identifier>
                    [--component terminal|cef]

            The macOS command refuses an existing destination and requires a complete
            self-contained publish payload, including the pinned native terminal runtime. It
            validates the reviewed managed catalog, native build receipt, and
            exact terminal-font and CEF runtime receipts,
            then writes deterministic evidence into the application bundle.
            """);
    }
}

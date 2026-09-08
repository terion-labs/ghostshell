namespace GhostShell.Packaging;

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
            Console.Error.WriteLine($"GhostSHELL packaging failed: {exception.Message}");
            return FailedExitCode;
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
            $"Created GhostShell.app {result.ProductVersion} "
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
            GhostSHELL packaging

              native-signature-removed-sha256 <Mach-O-file>

              macos --publish <native-aot-directory>
                    --managed-evidence <self-contained-directory>
                    --output <GhostShell.app>
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
                    --full-package <app.ghostshell-...-full.nupkg>
                    --app <extracted/GhostShell.app>
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

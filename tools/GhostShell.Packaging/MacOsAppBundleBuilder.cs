using System.Text;

namespace GhostShell.Packaging;

public sealed record MacOsAppBundleRequest(
    string PublishDirectory,
    string DestinationPath,
    string ProductVersion,
    string BuildVersion,
    string ProductIdentityManifestPath,
    string ProductIdentitySourceRoot,
    string AssetCatalogPath,
    string ComponentCatalogPath,
    string NativeComponentCatalogPath,
    string NativeBuildReceiptPath,
    string FontAssetsCatalogPath,
    string FontAssetsBuildReceiptPath,
    string NuGetPackageRoot,
    string? CefRuntimeRoot = null,
    string? CefRuntimeCatalogPath = null,
    string CefRuntimeIdentifier = "osx-arm64",
    string? ManagedEvidenceDirectory = null);

public sealed record MacOsAppBundleResult(
    string DestinationPath,
    string ProductVersion,
    string BuildVersion,
    int FileCount);

public sealed class MacOsAppBundleBuilder
{
    internal const int MaximumSourceFiles = 19_999;
    internal const int MaximumSourceEntries = 39_995;
    internal const int MaximumSourceDirectoryDepth = 62;
    internal const long MaximumPackageBytes = 8L * 1024 * 1024 * 1024;

    private const string InfoPlistResource =
        "GhostShell.Packaging.MacOS.Info.plist.template";
    private const string AppIconFileName = "GhostShell.icns";
    private const string AssetCatalogFileName = "Assets.car";
    private const string ProductIdentityFileName = "product-identity.json";
    private const string DistributionManifestFileName = "distribution.json";
    private const string ProductIdentityDirectoryName = "ProductIdentity";
    private const string ProductVersionPlaceholder = "__GHOSTSHELL_VERSION__";
    private const string BuildVersionPlaceholder = "__GHOSTSHELL_BUILD_VERSION__";
    private const int NativeEvidenceDirectoryCount = 1;
    private const int GeneratedBundleFileCount = 5;
    private const int GeneratedBundleDirectoryCount = 1;
    private const string NativeTerminalCatalogFileName =
        "native-terminal-components.json";
    private const string NativeTerminalReceiptFileName =
        "native-terminal-build-receipt.json";
    private const string TerminalFontCatalogFileName =
        "terminal-font-assets.json";
    private const string TerminalFontReceiptFileName =
        "terminal-font-assets-build-receipt.json";
    private const string TerminalFontLicenseFileName =
        "JETBRAINS-MONO-OFL.txt";
    private const string ProjectLicenseFileName = "GHOSTSHELL-LICENSE.txt";
    private const string MacOsLegalRecordFileName = "MACOS-RELEASE-LEGAL.json";
    private const string SmbSourceFileName = "SMBLIBRARY-SOURCE.json";
    private const string SmbRelinkingFileName =
        "SMBLIBRARY-SOURCE-AND-RELINKING.md";
    private const string SmbLicenseFileName = "SMBLIBRARY-LGPL-3.0.txt";
    private const string GplLicenseFileName = "GPL-3.0.txt";
    private const string NativeResourcesDirectoryName = "Native";
    private const string SqlLanguageResourcesDirectoryName = "SqlLanguage";

    private static readonly string[] RequiredRootFiles =
    [
        "GhostShell",
        "libghostty-vt.dylib",
        "GHOSTTY-LICENSE",
        "ghostty-vt-required-exports.txt",
        "THIRD-PARTY-NOTICES.md",
        "DOTNET-LICENSE.txt",
        "DOTNET-THIRD-PARTY-NOTICES.txt",
        NativeTerminalCatalogFileName,
        NativeTerminalReceiptFileName,
        TerminalFontCatalogFileName,
        TerminalFontReceiptFileName,
        TerminalFontLicenseFileName,
        ProjectLicenseFileName,
        MacOsLegalRecordFileName,
        SmbSourceFileName,
        SmbRelinkingFileName,
        SmbLicenseFileName,
        GplLicenseFileName,
    ];

    private static readonly IReadOnlyDictionary<string, string>
        LicenseDestinations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GHOSTTY-LICENSE"] = "GHOSTTY-LICENSE",
            ["THIRD-PARTY-NOTICES.md"] = "THIRD-PARTY-NOTICES.md",
            ["DOTNET-LICENSE.txt"] = "DOTNET-LICENSE.txt",
            ["DOTNET-THIRD-PARTY-NOTICES.txt"] =
                "DOTNET-THIRD-PARTY-NOTICES.txt",
            [NativeTerminalCatalogFileName] =
                Path.Combine("Native", NativeTerminalCatalogFileName),
            [NativeTerminalReceiptFileName] =
                Path.Combine("Native", NativeTerminalReceiptFileName),
            [TerminalFontCatalogFileName] =
                Path.Combine("Native", TerminalFontCatalogFileName),
            [TerminalFontReceiptFileName] =
                Path.Combine("Native", TerminalFontReceiptFileName),
            [TerminalFontLicenseFileName] = "JetBrainsMono-OFL.txt",
            [ProjectLicenseFileName] = ProjectLicenseFileName,
            [MacOsLegalRecordFileName] = MacOsLegalRecordFileName,
            [SmbSourceFileName] = SmbSourceFileName,
            [SmbRelinkingFileName] = SmbRelinkingFileName,
            [SmbLicenseFileName] = SmbLicenseFileName,
            [GplLicenseFileName] = GplLicenseFileName,
        };

    public MacOsAppBundleResult Build(MacOsAppBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.CefRuntimeIdentifier, "osx-arm64", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Full macOS application packaging currently supports only "
                + "osx-arm64; osx-x64 lacks a reviewed managed catalog and "
                + "libghostty-vt receipt.",
                nameof(request));
        }

        ValidateVersion(request.ProductVersion, nameof(request.ProductVersion), 3, 3);
        ValidateVersion(request.BuildVersion, nameof(request.BuildVersion), 1, 3);

        var identity = MacOsProductIdentity.Validate(
            request.ProductIdentityManifestPath,
            request.ProductIdentitySourceRoot,
            request.AssetCatalogPath);
        var legalClosure = MacOsReleaseLegalClosure.Validate(
            Path.Combine(
                request.ProductIdentitySourceRoot,
                "licenses",
                "macos-release-legal.json"),
            request.ProductIdentitySourceRoot);
        var infoPlist = RenderInfoPlist(request.ProductVersion, request.BuildVersion);
        var distributionManifest = DistributionManifestBuilder.BuildGitHubVelopack(
            request.CefRuntimeIdentifier);
        var generatedBundleBytes = checked(
            Encoding.UTF8.GetByteCount(infoPlist)
            + identity.Manifest.Length
            + identity.IcnsFallback.Length
            + identity.AssetCatalog.Length
            + distributionManifest.Length);
        var publishDirectory = MacOsPackagePaths.RequireExistingDirectory(
            request.PublishDirectory,
            nameof(request.PublishDirectory));
        var managedEvidenceDirectory = request.ManagedEvidenceDirectory is null
            ? publishDirectory
            : MacOsPackagePaths.RequireExistingDirectory(
                request.ManagedEvidenceDirectory,
                nameof(request.ManagedEvidenceDirectory));
        var hasSeparateManagedEvidence = !MacOsPackagePaths.AreSameDirectory(
            publishDirectory,
            managedEvidenceDirectory);
        var destinationPath = MacOsPackagePaths.RequireDestination(request.DestinationPath);
        MacOsPackagePaths.ValidateSeparateTrees(publishDirectory, destinationPath);
        if (hasSeparateManagedEvidence)
        {
            MacOsPackagePaths.ValidateSeparateTrees(
                publishDirectory,
                managedEvidenceDirectory);
            MacOsPackagePaths.ValidateSeparateTrees(
                managedEvidenceDirectory,
                destinationPath);
        }
        var cefPlan = CreateCefPlan(request, publishDirectory, destinationPath);

        var sourceEntries = InspectPublishDirectory(
            publishDirectory,
            generatedBundleBytes);
        ValidateRequiredPayload(sourceEntries, hasSeparateManagedEvidence);
        ValidatePackagedLegalEvidence(publishDirectory, legalClosure);
        var evidenceLimits = CreateManagedEvidenceLimits(
            sourceEntries,
            generatedBundleBytes);

        var destinationParent = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException(
                "The macOS bundle destination must have a parent directory.",
                nameof(request));
        var stagingPath = Path.Combine(
            destinationParent,
            $".{MacOsPackagePaths.BundleName}.{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stagingPath);
        var published = false;
        try
        {
            var contentsDirectory = Path.Combine(stagingPath, "Contents");
            var executableDirectory = Path.Combine(contentsDirectory, "MacOS");
            var resourcesDirectory = Path.Combine(contentsDirectory, "Resources");
            var licenseDirectory = Path.Combine(resourcesDirectory, "Licenses");
            Directory.CreateDirectory(executableDirectory);
            Directory.CreateDirectory(licenseDirectory);

            CopyPublishPayload(
                executableDirectory,
                resourcesDirectory,
                licenseDirectory,
                sourceEntries);
            ValidateNativeProvenance();
            var managedEvidence = ManagedComponentEvidenceBuilder.Build(
                managedEvidenceDirectory,
                licenseDirectory,
                request.ComponentCatalogPath,
                request.NuGetPackageRoot,
                request.ProductVersion,
                evidenceLimits);
            EnsureEvidenceDestinationsAreAvailable(
                licenseDirectory,
                managedEvidence.Files);
            ValidateFinalBudget(
                sourceEntries,
                managedEvidence.Files,
                generatedBundleBytes,
                cefPlan);
            WriteManagedEvidence(licenseDirectory, managedEvidence.Files);
            cefPlan?.CopyTo(contentsDirectory);
            WriteNewFile(
                Path.Combine(resourcesDirectory, AppIconFileName),
                identity.IcnsFallback);
            WriteNewFile(
                Path.Combine(resourcesDirectory, AssetCatalogFileName),
                identity.AssetCatalog);
            WriteNewFile(
                Path.Combine(resourcesDirectory, DistributionManifestFileName),
                distributionManifest);
            var identityDirectory = Path.Combine(
                licenseDirectory,
                ProductIdentityDirectoryName);
            Directory.CreateDirectory(identityDirectory);
            WriteNewFile(
                Path.Combine(identityDirectory, ProductIdentityFileName),
                identity.Manifest);
            WriteNewFile(
                Path.Combine(contentsDirectory, "Info.plist"),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    .GetBytes(infoPlist));
            ValidateNativeProvenance();

            ExclusiveDirectoryMover.Move(stagingPath, destinationPath);
            published = true;
            return new MacOsAppBundleResult(
                destinationPath,
                request.ProductVersion,
                request.BuildVersion,
                sourceEntries.Count(entry => !entry.IsDirectory)
                + managedEvidence.Files.Count
                + (cefPlan?.FileCount ?? 0)
                + GeneratedBundleFileCount);

            void ValidateNativeProvenance()
            {
                NativeTerminalPackageProvenance.Validate(
                    executableDirectory,
                    resourcesDirectory,
                    Path.Combine(resourcesDirectory, NativeResourcesDirectoryName),
                    licenseDirectory,
                    request.NativeComponentCatalogPath,
                    request.NativeBuildReceiptPath);
                TerminalFontPackageProvenance.Validate(
                    resourcesDirectory,
                    licenseDirectory,
                    request.FontAssetsCatalogPath,
                    request.FontAssetsBuildReceiptPath);
            }
        }
        finally
        {
            if (!published && Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    internal static void ValidateSourceBudget(
        int fileCount,
        int entryCount,
        int maximumDirectoryDepth,
        long sourceBytes,
        int generatedBundleBytes)
    {
        if (fileCount < 0 || fileCount > MaximumSourceFiles)
        {
            throw new InvalidDataException(
                $"The publish payload exceeds {MaximumSourceFiles} files.");
        }

        if (entryCount < 0 || entryCount > MaximumSourceEntries)
        {
            throw new InvalidDataException(
                $"The publish payload exceeds {MaximumSourceEntries} entries.");
        }

        if (maximumDirectoryDepth < 0
            || maximumDirectoryDepth > MaximumSourceDirectoryDepth)
        {
            throw new InvalidDataException(
                $"The publish payload exceeds {MaximumSourceDirectoryDepth} directory levels.");
        }

        if (generatedBundleBytes < 0
            || sourceBytes < 0
            || sourceBytes > MaximumPackageBytes - generatedBundleBytes)
        {
            throw new InvalidDataException(
                $"The finished application bundle exceeds {MaximumPackageBytes} bytes.");
        }
    }

    private static IReadOnlyList<SourceEntry> InspectPublishDirectory(
        string publishDirectory,
        int generatedBundleBytes)
    {
        var root = new DirectoryInfo(publishDirectory);
        var entries = new List<SourceEntry>();
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, 0));
        var entryCount = 0;
        var fileCount = 0;
        var maximumDirectoryDepth = 0;
        long sourceBytes = 0;

        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos(
                         "*",
                         new EnumerationOptions
                         {
                             AttributesToSkip = FileAttributes.None,
                             IgnoreInaccessible = false,
                             RecurseSubdirectories = false,
                             ReturnSpecialDirectories = false,
                         }))
            {
                entryCount++;
                if (entryCount > MaximumSourceEntries)
                {
                    ValidateSourceBudget(
                        fileCount,
                        entryCount,
                        maximumDirectoryDepth,
                        sourceBytes,
                        generatedBundleBytes);
                }

                if (entry.LinkTarget is not null
                    || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        "The publish payload contains a symbolic link or reparse point.");
                }

                var relativePath = Path.GetRelativePath(root.FullName, entry.FullName);
                if (entry is DirectoryInfo childDirectory)
                {
                    if (childDirectory.Name.EndsWith(
                            ".dSYM",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Debug symbol bundles must be published outside the application bundle.");
                    }

                    var childDepth = depth + 1;
                    maximumDirectoryDepth = Math.Max(maximumDirectoryDepth, childDepth);
                    if (childDepth > MaximumSourceDirectoryDepth)
                    {
                        ValidateSourceBudget(
                            fileCount,
                            entryCount,
                            maximumDirectoryDepth,
                            sourceBytes,
                            generatedBundleBytes);
                    }

                    entries.Add(new SourceEntry(
                        entry.FullName,
                        relativePath,
                        IsDirectory: true,
                        Length: 0,
                        UnixMode: null));
                    pending.Push((childDirectory, childDepth));
                    continue;
                }

                if (entry is not FileInfo)
                {
                    throw new InvalidDataException(
                        "The publish payload contains an unsupported filesystem entry.");
                }

                fileCount++;
                if (fileCount > MaximumSourceFiles)
                {
                    ValidateSourceBudget(
                        fileCount,
                        entryCount,
                        maximumDirectoryDepth,
                        sourceBytes,
                        generatedBundleBytes);
                }

                using var stream = RegularPackageFileReader.Open(
                    entry.FullName,
                    out var inspection);
                try
                {
                    sourceBytes = checked(sourceBytes + inspection.Length);
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException(
                        "The publish payload byte count overflowed.",
                        exception);
                }

                ValidateSourceBudget(
                    fileCount,
                    entryCount,
                    maximumDirectoryDepth,
                    sourceBytes,
                    generatedBundleBytes);
                entries.Add(new SourceEntry(
                    entry.FullName,
                    relativePath,
                    IsDirectory: false,
                    inspection.Length,
                    inspection.UnixMode));
            }
        }

        ValidateSourceBudget(
            fileCount,
            entryCount,
            maximumDirectoryDepth,
            sourceBytes,
            generatedBundleBytes);
        return entries;
    }

    private static void ValidateRequiredPayload(
        IReadOnlyList<SourceEntry> entries,
        bool hasSeparateManagedEvidence)
    {
        var rootFiles = entries
            .Where(entry =>
                !entry.IsDirectory
                && string.IsNullOrEmpty(Path.GetDirectoryName(entry.RelativePath)))
            .ToDictionary(
                entry => Path.GetFileName(entry.RelativePath),
                StringComparer.Ordinal);
        foreach (var requiredFile in RequiredRootFiles)
        {
            if (!rootFiles.ContainsKey(requiredFile))
            {
                throw new InvalidDataException(
                    $"The publish payload is missing required file {requiredFile}.");
            }
        }

        if (hasSeparateManagedEvidence)
        {
            var managedHostEntry = entries.FirstOrDefault(entry =>
                !entry.IsDirectory
                && (entry.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || entry.RelativePath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                    || entry.RelativePath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                    || entry.RelativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)));
            if (managedHostEntry is not null)
            {
                throw new InvalidDataException(
                    $"The Native AOT publish contains managed host file {managedHostEntry.RelativePath}.");
            }
        }
        else
        {
            foreach (var managedHostFile in new[]
                     {
                         "GhostShell.deps.json",
                         "GhostShell.runtimeconfig.json",
                     })
            {
                if (!rootFiles.ContainsKey(managedHostFile))
                {
                    throw new InvalidDataException(
                        $"The publish payload is missing required file {managedHostFile}.");
                }
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode executeBits =
                UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            if ((rootFiles["GhostShell"].UnixMode & executeBits) == 0)
            {
                throw new InvalidDataException(
                    "The published GhostShell executable lacks an execute bit.");
            }
        }
    }

    private static void CopyPublishPayload(
        string executableDirectory,
        string resourcesDirectory,
        string licenseDirectory,
        IReadOnlyList<SourceEntry> entries)
    {
        foreach (var directory in entries
                     .Where(entry => entry.IsDirectory)
                     .OrderBy(entry => PathDepth(entry.RelativePath)))
        {
            Directory.CreateDirectory(ResolvePayloadDestination(
                executableDirectory,
                resourcesDirectory,
                directory.RelativePath));
        }

        foreach (var file in entries.Where(entry => !entry.IsDirectory))
        {
            var fileName = Path.GetFileName(file.RelativePath);
            var isRootFile =
                string.IsNullOrEmpty(Path.GetDirectoryName(file.RelativePath));
            string destination;
            if (isRootFile
                && LicenseDestinations.TryGetValue(
                    fileName,
                    out var licenseDestination))
            {
                destination = Path.Combine(licenseDirectory, licenseDestination);
            }
            else if (string.Equals(
                         file.RelativePath,
                         "ghostty-vt-required-exports.txt",
                         StringComparison.Ordinal))
            {
                destination = Path.Combine(
                    resourcesDirectory,
                    NativeResourcesDirectoryName,
                    file.RelativePath);
            }
            else if (IsSqlLanguageMetadata(file.RelativePath))
            {
                destination = Path.Combine(
                    resourcesDirectory,
                    NativeResourcesDirectoryName,
                    SqlLanguageResourcesDirectoryName,
                    fileName);
            }
            else
            {
                destination = ResolvePayloadDestination(
                    executableDirectory,
                    resourcesDirectory,
                    file.RelativePath);
            }
            var destinationParent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException(
                    "A publish payload entry has no destination parent.");
            Directory.CreateDirectory(destinationParent);

            using var source = RegularPackageFileReader.Open(
                file.FullPath,
                out var current);
            if (current.Length != file.Length || current.UnixMode != file.UnixMode)
            {
                throw new InvalidDataException(
                    "The publish payload changed while the bundle was assembled.");
            }

            using (var target = new FileStream(
                       destination,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 131_072,
                       FileOptions.SequentialScan))
            {
                CopyExactly(source, target, file.Length);
            }

            if (!OperatingSystem.IsWindows()
                && current.UnixMode is { } unixMode)
            {
                File.SetUnixFileMode(destination, unixMode);
            }
        }
    }

    private static void ValidatePackagedLegalEvidence(
        string publishDirectory,
        MacOsReleaseLegalInspection legalClosure)
    {
        var requiredFiles = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [MacOsLegalRecordFileName] = legalClosure.Record,
            [ProjectLicenseFileName] = legalClosure.Evidence["LICENSE"],
            [GplLicenseFileName] = legalClosure.Evidence["licenses/GPL-3.0.txt"],
            [SmbLicenseFileName] =
                legalClosure.Evidence["licenses/SMBLIBRARY-LGPL-3.0.txt"],
            [SmbRelinkingFileName] = legalClosure.Evidence[
                "licenses/SMBLIBRARY-SOURCE-AND-RELINKING.md"],
            [SmbSourceFileName] =
                legalClosure.Evidence["licenses/SMBLIBRARY-SOURCE.json"],
            ["THIRD-PARTY-NOTICES.md"] =
                legalClosure.Evidence["licenses/THIRD-PARTY-NOTICES.md"],
        };
        foreach (var (fileName, expected) in requiredFiles)
        {
            var path = Path.Combine(publishDirectory, fileName);
            using var stream = RegularPackageFileReader.Open(path, out var inspection);
            if (inspection.Length != expected.Length)
            {
                throw new InvalidDataException(
                    $"The packaged legal file {fileName} differs from reviewed evidence.");
            }

            var actual = new byte[checked((int)inspection.Length)];
            stream.ReadExactly(actual);
            if (!actual.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidDataException(
                    $"The packaged legal file {fileName} differs from reviewed evidence.");
            }
        }
    }

    private static string ResolvePayloadDestination(
        string executableDirectory,
        string resourcesDirectory,
        string relativePath)
    {
        var topLevelDirectory = relativePath.Split(
            Path.DirectorySeparatorChar,
            2)[0];
        var root = topLevelDirectory is "connection-engine-legal" or "workspace-runtime-legal" or "fonts" or "ghostty"
            ? resourcesDirectory
            : executableDirectory;
        var workspaceRuntime = Path.Combine("runtimes", "osx-arm64", "workspace-runtime")
            + Path.DirectorySeparatorChar;
        if (relativePath.StartsWith(workspaceRuntime, StringComparison.Ordinal)
            && !string.Equals(relativePath, workspaceRuntime + "workspace-runtime", StringComparison.Ordinal)
            && !string.Equals(Path.GetExtension(relativePath), ".dylib", StringComparison.Ordinal))
        {
            // Boot images and dependency privacy manifests are resources, not host code.
            root = resourcesDirectory;
        }
        if (relativePath.StartsWith(Path.Combine("runtimes", "osx-arm64", "native")
                + Path.DirectorySeparatorChar + "workspace-network-gateway-", StringComparison.Ordinal))
        {
            root = resourcesDirectory;
        }
        return Path.Combine(root, relativePath);
    }

    private static bool IsSqlLanguageMetadata(string relativePath)
    {
        var directory = Path.Combine("runtimes", "osx-arm64", "native");
        var parent = Path.GetDirectoryName(relativePath);
        if (!string.Equals(parent, directory, StringComparison.Ordinal))
        {
            return false;
        }

        return Path.GetFileName(relativePath) is
            "THIRD-PARTY-NOTICES.md"
            or "runtime-dependencies.txt"
            or "build-receipt.json";
    }

    private static void ValidateFinalBudget(
        IReadOnlyList<SourceEntry> sourceEntries,
        IReadOnlyList<ManagedComponentEvidenceFile> evidenceFiles,
        int generatedBundleBytes,
        CefMacOsBundlePlan? cefPlan)
    {
        var evidenceDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidenceFile in evidenceFiles)
        {
            var parent = Path.GetDirectoryName(evidenceFile.RelativePath);
            while (!string.IsNullOrEmpty(parent))
            {
                evidenceDirectories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        var maximumEvidenceDepth = evidenceFiles.Count == 0
            ? 0
            : evidenceFiles.Max(file => PathDepth(file.RelativePath));
        var maximumSourceDepth = sourceEntries.Count == 0
            ? 0
            : sourceEntries.Max(entry => PathDepth(entry.RelativePath));
        long bytes;
        try
        {
            bytes = checked(
                sourceEntries.Where(entry => !entry.IsDirectory)
                    .Sum(entry => entry.Length)
                + evidenceFiles.Sum(file => (long)file.Content.Length)
                + (cefPlan?.TotalBytes ?? 0));
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "The finished application bundle byte count overflowed.",
                exception);
        }

        ValidateSourceBudget(
            sourceEntries.Count(entry => !entry.IsDirectory)
            + evidenceFiles.Count
            + (cefPlan?.FileCount ?? 0)
            + GeneratedBundleFileCount,
            sourceEntries.Count
            + evidenceFiles.Count
            + evidenceDirectories.Count
            + NativeEvidenceDirectoryCount
            + GeneratedBundleFileCount
            + GeneratedBundleDirectoryCount
            + CountCefDirectories(cefPlan),
            Math.Max(
                Math.Max(
                    Math.Max(maximumSourceDepth, maximumEvidenceDepth),
                    cefPlan?.MaximumRelativePathDepth ?? 0),
                NativeEvidenceDirectoryCount),
            bytes,
            generatedBundleBytes);
    }

    private static CefMacOsBundlePlan? CreateCefPlan(
        MacOsAppBundleRequest request,
        string publishDirectory,
        string destinationPath)
    {
        if (request.CefRuntimeRoot is null && request.CefRuntimeCatalogPath is null)
        {
            return null;
        }

        if (request.CefRuntimeRoot is null || request.CefRuntimeCatalogPath is null)
        {
            throw new ArgumentException(
                "The CEF runtime root and catalog must be supplied together.",
                nameof(request));
        }

        var runtimeRoot = MacOsPackagePaths.RequireExistingDirectory(
            request.CefRuntimeRoot,
            nameof(request.CefRuntimeRoot));
        MacOsPackagePaths.ValidateSeparateTrees(runtimeRoot, publishDirectory);
        MacOsPackagePaths.ValidateSeparateTrees(runtimeRoot, destinationPath);
        return CefMacOsBundlePlan.Create(
            runtimeRoot,
            request.CefRuntimeCatalogPath,
            request.CefRuntimeIdentifier);
    }

    private static int CountCefDirectories(CefMacOsBundlePlan? plan)
    {
        if (plan is null)
        {
            return 0;
        }

        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in plan.Files.Select(file => file.DestinationRelativePath)
                     .Concat(plan.GeneratedFiles.Select(file => file.DestinationRelativePath)))
        {
            var parent = Path.GetDirectoryName(path.Replace(
                '/',
                Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        return directories.Count;
    }

    private static ManagedComponentEvidenceLimits CreateManagedEvidenceLimits(
        IReadOnlyList<SourceEntry> sourceEntries,
        int generatedBundleBytes)
    {
        var sourceFileCount = sourceEntries.Count(entry => !entry.IsDirectory);
        var sourceBytes = sourceEntries
            .Where(entry => !entry.IsDirectory)
            .Sum(entry => entry.Length);
        var remainingBytes = MaximumPackageBytes - generatedBundleBytes - sourceBytes;
        var remainingFiles = MaximumSourceFiles - sourceFileCount;
        var remainingEntries = MaximumSourceEntries
            - sourceEntries.Count
            - NativeEvidenceDirectoryCount;
        if (remainingFiles < 1 || remainingEntries < 1 || remainingBytes < 1)
        {
            throw new InvalidDataException(
                "The publish payload leaves no room for required managed evidence.");
        }

        return new ManagedComponentEvidenceLimits(
            Math.Min(
                ManagedComponentEvidenceBuilder.MaximumGeneratedEvidenceFiles,
                remainingFiles),
            remainingEntries,
            Math.Min(
                ManagedComponentEvidenceBuilder.MaximumGeneratedEvidenceBytes,
                remainingBytes),
            MaximumSourceDirectoryDepth - 1);
    }

    private static void WriteManagedEvidence(
        string licenseDirectory,
        IReadOnlyList<ManagedComponentEvidenceFile> evidenceFiles)
    {
        foreach (var evidenceFile in evidenceFiles)
        {
            var destination = Path.Combine(
                licenseDirectory,
                evidenceFile.RelativePath);
            var destinationParent = Path.GetDirectoryName(destination)
                ?? throw new InvalidDataException(
                    "A managed evidence file has no destination parent.");
            Directory.CreateDirectory(destinationParent);
            using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 131_072,
                FileOptions.SequentialScan);
            target.Write(evidenceFile.Content);
        }
    }

    private static void WriteNewFile(string path, byte[] content)
    {
        using var target = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        target.Write(content);
    }

    private static void EnsureEvidenceDestinationsAreAvailable(
        string licenseDirectory,
        IReadOnlyList<ManagedComponentEvidenceFile> evidenceFiles)
    {
        foreach (var evidenceFile in evidenceFiles)
        {
            var destination = Path.Combine(
                licenseDirectory,
                evidenceFile.RelativePath);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new InvalidDataException(
                    $"Generated evidence path {evidenceFile.RelativePath} "
                    + "collides with the publish payload.");
            }

            var parent = Path.GetDirectoryName(destination);
            while (parent is not null
                   && !string.Equals(
                       parent,
                       licenseDirectory,
                       StringComparison.Ordinal))
            {
                if (File.Exists(parent))
                {
                    throw new InvalidDataException(
                        $"Generated evidence path {evidenceFile.RelativePath} "
                        + "collides with the publish payload.");
                }

                parent = Path.GetDirectoryName(parent);
            }
        }
    }

    private static void CopyExactly(Stream source, Stream destination, long length)
    {
        var buffer = new byte[131_072];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(
                buffer,
                0,
                (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new InvalidDataException(
                    "A publish payload file became shorter while it was copied.");
            }

            destination.Write(buffer, 0, read);
            remaining -= read;
        }

        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "A publish payload file became longer while it was copied.");
        }
    }

    private static int PathDepth(string path) =>
        path.Count(character =>
            character == Path.DirectorySeparatorChar
            || character == Path.AltDirectorySeparatorChar);

    private static string RenderInfoPlist(
        string productVersion,
        string buildVersion)
    {
        using var stream = typeof(MacOsAppBundleBuilder).Assembly
            .GetManifestResourceStream(InfoPlistResource)
            ?? throw new InvalidOperationException(
                "The embedded macOS Info.plist template is unavailable.");
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        var template = reader.ReadToEnd();
        if (CountOccurrences(template, ProductVersionPlaceholder) != 1
            || CountOccurrences(template, BuildVersionPlaceholder) != 1)
        {
            throw new InvalidDataException(
                "The embedded macOS Info.plist template has invalid placeholders.");
        }

        return template
            .Replace(
                ProductVersionPlaceholder,
                productVersion,
                StringComparison.Ordinal)
            .Replace(
                BuildVersionPlaceholder,
                buildVersion,
                StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }

        return count;
    }

    private static void ValidateVersion(
        string value,
        string parameterName,
        int minimumParts,
        int maximumParts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 32)
        {
            throw new ArgumentException(
                "The bundle version is too long.",
                parameterName);
        }

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length < minimumParts
            || parts.Length > maximumParts
            || parts.Any(part =>
                part.Length == 0
                || part.Length > 9
                || !part.All(character => character is >= '0' and <= '9')))
        {
            throw new ArgumentException(
                "The bundle version must contain dot-separated unsigned integers.",
                parameterName);
        }
    }

    private sealed record SourceEntry(
        string FullPath,
        string RelativePath,
        bool IsDirectory,
        long Length,
        UnixFileMode? UnixMode);
}

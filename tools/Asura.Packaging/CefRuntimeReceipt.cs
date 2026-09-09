using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Asura.Packaging;

internal sealed record CefRuntimeFile(
    string RelativePath,
    string FullPath,
    long Length,
    string Sha256,
    UnixFileMode? UnixMode);

internal sealed record CefRuntimeInspection(
    string RuntimeRoot,
    string Rid,
    CefRuntimeCatalog Catalog,
    string ArchiveSha1,
    string ArchiveSha256,
    string PatchSetSha256,
    string SourceSnapshotSha256,
    byte[] ReceiptContent,
    IReadOnlyList<CefRuntimeFile> Files);

internal static class CefRuntimeReceipt
{
    public const string FileName = "cef-runtime-build-receipt.json";

    private const int MaximumFiles = 20_000;
    private const int MaximumEntries = 40_000;
    private const int MaximumDirectoryDepth = 64;
    private const long MaximumBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumReceiptBytes = 16 * 1024 * 1024;

    private static readonly string[] MacHelperSuffixes =
    [
        string.Empty,
        " (Alerts)",
        " (GPU)",
        " (Plugin)",
        " (Renderer)",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 12,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static void Create(
        string runtimeRoot,
        string catalogPath,
        string rid,
        string archiveSha1,
        string archiveSha256,
        string patchSetSha256,
        string sourceSnapshotSha256,
        string outputPath)
    {
        var root = MacOsPackagePaths.RequireExistingDirectory(
            runtimeRoot,
            nameof(runtimeRoot));
        var expectedOutput = Path.Combine(root, FileName);
        var outputFullPath = Path.GetFullPath(outputPath);
        var outputParent = Path.GetDirectoryName(outputFullPath);
        if (!string.Equals(Path.GetFileName(outputFullPath), FileName
, StringComparison.Ordinal) || outputParent is null
            || !MacOsPackagePaths.AreSameDirectory(
                root,
                MacOsPackagePaths.RequireExistingDirectory(
                    outputParent,
                    nameof(outputPath))))
        {
            throw new ArgumentException(
                $"The CEF receipt output must be {FileName} in the runtime root.",
                nameof(outputPath));
        }

        if (File.Exists(expectedOutput) || Directory.Exists(expectedOutput))
        {
            throw new IOException("The CEF runtime receipt already exists.");
        }

        CefRuntimeCatalog.ValidateHex(archiveSha1, 40, "archiveSha1");
        CefRuntimeCatalog.ValidateHex(archiveSha256, 64, "archiveSha256");
        CefRuntimeCatalog.ValidateHex(patchSetSha256, 64, "patchSetSha256");
        CefRuntimeCatalog.ValidateHex(
            sourceSnapshotSha256,
            64,
            "sourceSnapshotSha256");
        var catalog = CefRuntimeCatalog.Read(catalogPath);
        var distribution = RequireDistribution(catalog, rid);
        if (!string.Equals(
                distribution.ArchiveSha1,
                archiveSha1,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The downloaded CEF archive does not match the reviewed SHA-1.");
        }

        if (!string.Equals(
                distribution.ArchiveSha256,
                archiveSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The downloaded CEF archive does not match the reviewed SHA-256.");
        }

        if (!string.Equals(
                catalog.BindingPatchSetSha256,
                patchSetSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CEF binding patch-set digest does not match the reviewed catalog.");
        }

        if (!string.Equals(
                catalog.BindingSourceSnapshotSha256,
                sourceSnapshotSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CEF binding source-snapshot digest does not match the reviewed catalog.");
        }

        var files = InspectFiles(root, includeReceipt: false);
        ValidatePayload(files, rid, catalog);
        var receipt = WriteReceipt(
            catalog,
            distribution,
            archiveSha256,
            patchSetSha256,
            sourceSnapshotSha256,
            files);
        using var output = new FileStream(
            expectedOutput,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        output.Write(receipt);
    }

    public static CefRuntimeInspection Validate(
        string runtimeRoot,
        string catalogPath,
        string rid)
    {
        var root = MacOsPackagePaths.RequireExistingDirectory(
            runtimeRoot,
            nameof(runtimeRoot));
        var catalog = CefRuntimeCatalog.Read(catalogPath);
        var distribution = RequireDistribution(catalog, rid);
        var receiptPath = Path.Combine(root, FileName);
        var receiptContent = ReadBoundedFile(
            receiptPath,
            MaximumReceiptBytes,
            "CEF runtime receipt");
        var receipt = ParseReceipt(receiptContent);
        ValidateReceiptIdentity(receipt, catalog, distribution);

        var files = InspectFiles(root, includeReceipt: false);
        ValidatePayload(files, rid, catalog);
        ValidateReceiptFiles(
            receipt.Files
                ?? throw new InvalidDataException(
                    "The CEF runtime receipt has no file closure."),
            files);
        return new CefRuntimeInspection(
            root,
            rid,
            catalog,
            receipt.ArchiveSha1,
            receipt.ArchiveSha256,
            receipt.PatchSetSha256,
            receipt.BindingSourceSnapshotSha256,
            receiptContent,
            files);
    }

    private static ReceiptDocument ParseReceipt(byte[] content)
    {
        try
        {
            using var parsed = JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 12,
                });
            ValidateNoDuplicateProperties(parsed.RootElement);
            return JsonSerializer.Deserialize<ReceiptDocument>(content, JsonOptions)
                ?? throw new InvalidDataException("The CEF runtime receipt is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The CEF runtime receipt is malformed.",
                exception);
        }
    }

    private static void ValidateReceiptIdentity(
        ReceiptDocument receipt,
        CefRuntimeCatalog catalog,
        CefRuntimeDistribution distribution)
    {
        if (!receipt.BuildSucceeded
            || receipt.SchemaVersion != 1
            || !string.Equals(receipt.Rid, distribution.Rid
, StringComparison.Ordinal) || !string.Equals(receipt.Platform, distribution.Platform
, StringComparison.Ordinal) || !string.Equals(receipt.CefVersion, catalog.CefVersion
, StringComparison.Ordinal) || !string.Equals(receipt.BindingRepository, catalog.BindingRepository
, StringComparison.Ordinal) || !string.Equals(receipt.BindingCommit, catalog.BindingCommit
, StringComparison.Ordinal) || !string.Equals(receipt.BindingVersion, catalog.BindingVersion
, StringComparison.Ordinal) || !string.Equals(receipt.ArchiveSha1, distribution.ArchiveSha1, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CEF runtime receipt identity does not match the reviewed catalog.");
        }

        var catalogSha256 = Sha256(catalog.Content);
        if (!string.Equals(receipt.CatalogSha256, catalogSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CEF runtime receipt does not bind the reviewed catalog bytes.");
        }

        CefRuntimeCatalog.ValidateHex(receipt.ArchiveSha256, 64, "archiveSha256");
        CefRuntimeCatalog.ValidateHex(receipt.PatchSetSha256, 64, "patchSetSha256");
        CefRuntimeCatalog.ValidateHex(
            receipt.BindingSourceSnapshotSha256,
            64,
            "sourceSnapshotSha256");
        if (!string.Equals(receipt.PatchSetSha256, catalog.BindingPatchSetSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CEF runtime receipt patch set does not match the reviewed catalog.");
        }

        if (!string.Equals(receipt.ArchiveSha256, distribution.ArchiveSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CEF runtime receipt archive does not match the reviewed catalog.");
        }

        if (!string.Equals(receipt.BindingSourceSnapshotSha256
, catalog.BindingSourceSnapshotSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The CEF runtime receipt source snapshot does not match the reviewed catalog.");
        }
    }

    private static IReadOnlyList<CefRuntimeFile> InspectFiles(
        string root,
        bool includeReceipt)
    {
        var files = new List<CefRuntimeFile>();
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        var entryCount = 0;
        long totalBytes = 0;

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
                if (entryCount > MaximumEntries)
                {
                    throw new InvalidDataException(
                        $"The CEF runtime exceeds {MaximumEntries} entries.");
                }

                if (entry.LinkTarget is not null
                    || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidDataException(
                        "The CEF runtime contains a symbolic link or reparse point.");
                }

                var relativePath = NormalizeRelativePath(
                    Path.GetRelativePath(root, entry.FullName));
                if (entry is DirectoryInfo childDirectory)
                {
                    if (depth + 1 > MaximumDirectoryDepth)
                    {
                        throw new InvalidDataException(
                            $"The CEF runtime exceeds {MaximumDirectoryDepth} directory levels.");
                    }

                    pending.Push((childDirectory, depth + 1));
                    continue;
                }

                if (entry is not FileInfo)
                {
                    throw new InvalidDataException(
                        "The CEF runtime contains an unsupported filesystem entry.");
                }

                if (!includeReceipt
                    && string.Equals(relativePath, FileName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (files.Count >= MaximumFiles)
                {
                    throw new InvalidDataException(
                        $"The CEF runtime exceeds {MaximumFiles} files.");
                }

                using var stream = RegularPackageFileReader.Open(
                    entry.FullName,
                    out var inspection);
                if (inspection.Length <= 0)
                {
                    throw new InvalidDataException(
                        $"CEF runtime file {relativePath} is empty.");
                }

                totalBytes = checked(totalBytes + inspection.Length);
                if (totalBytes > MaximumBytes)
                {
                    throw new InvalidDataException(
                        $"The CEF runtime exceeds {MaximumBytes} bytes.");
                }

                var hash = Convert.ToHexString(SHA256.HashData(stream))
                    .ToLowerInvariant();
                if (stream.ReadByte() != -1)
                {
                    throw new InvalidDataException(
                        $"CEF runtime file {relativePath} changed while it was hashed.");
                }

                files.Add(new CefRuntimeFile(
                    relativePath,
                    entry.FullName,
                    inspection.Length,
                    hash,
                    inspection.UnixMode));
            }
        }

        return [.. files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)];
    }

    private static void ValidatePayload(
        IReadOnlyList<CefRuntimeFile> files,
        string rid,
        CefRuntimeCatalog catalog)
    {
        var paths = files.ToDictionary(
            file => file.RelativePath,
            StringComparer.Ordinal);
        RequireFile(paths, "CEF-LICENSE.txt");
        RequireFile(paths, "CEF-CREDITS.html");
        RequireFile(paths, "EXCLR8CEF-LICENSE.txt");
        var distribution = RequireDistribution(catalog, rid);
        RequireHash(paths, "CEF-LICENSE.txt", distribution.CefLicenseSha256);
        RequireHash(paths, "CEF-CREDITS.html", distribution.CefCreditsSha256);
        RequireHash(
            paths,
            "EXCLR8CEF-LICENSE.txt",
            catalog.BindingLicenseSha256);

        if (rid.StartsWith("osx-", StringComparison.Ordinal))
        {
            ValidateMacPayload(paths, rid);
            return;
        }

        if (string.Equals(rid, "win-x64", StringComparison.Ordinal))
        {
            RequireFiles(
                paths,
                "exclr8cef.dll",
                "libcef.dll",
                "chrome_elf.dll",
                "d3dcompiler_47.dll",
                "dxcompiler.dll",
                "dxil.dll",
                "libEGL.dll",
                "libGLESv2.dll",
                "vk_swiftshader.dll",
                "vk_swiftshader_icd.json",
                "vulkan-1.dll",
                "icudtl.dat",
                "resources.pak",
                "chrome_100_percent.pak",
                "chrome_200_percent.pak",
                "v8_context_snapshot.bin",
                "locales/en-US.pak");
            foreach (var path in paths.Keys.Where(path =>
                         path.EndsWith(".dll", StringComparison.Ordinal)))
            {
                ValidatePeX64(paths[path]);
            }

            return;
        }

        RequireFiles(
            paths,
            "libexclr8cef.so",
            "libcef.so",
            "libEGL.so",
            "libGLESv2.so",
            "libvk_swiftshader.so",
            "libvulkan.so.1",
            "vk_swiftshader_icd.json",
            "chrome-sandbox",
            "icudtl.dat",
            "resources.pak",
            "chrome_100_percent.pak",
            "chrome_200_percent.pak",
            "v8_context_snapshot.bin",
            "locales/en-US.pak");
        RequireExecutable(paths["chrome-sandbox"]);
        foreach (var path in paths.Keys.Where(path =>
                     path.EndsWith(".so", StringComparison.Ordinal)
                     || path.EndsWith(".so.1", StringComparison.Ordinal)
                     || string.Equals(path, "chrome-sandbox", StringComparison.Ordinal)))
        {
            ValidateElf(paths[path], rid);
        }
    }

    private static void ValidateMacPayload(
        IReadOnlyDictionary<string, CefRuntimeFile> paths,
        string rid)
    {
        const string framework = "Chromium Embedded Framework.framework";
        var snapshotArchitecture = rid switch
        {
            "osx-arm64" => "arm64",
            "osx-x64" => "x86_64",
            _ => throw new InvalidOperationException(
                "Unexpected macOS CEF runtime identifier."),
        };
        RequireFiles(
            paths,
            "libexclr8cef.dylib",
            $"{framework}/Chromium Embedded Framework",
            $"{framework}/Libraries/libEGL.dylib",
            $"{framework}/Libraries/libGLESv2.dylib",
            $"{framework}/Libraries/libcef_sandbox.dylib",
            $"{framework}/Libraries/libvk_swiftshader.dylib",
            $"{framework}/Libraries/vk_swiftshader_icd.json",
            $"{framework}/Resources/Info.plist",
            $"{framework}/Resources/chrome_100_percent.pak",
            $"{framework}/Resources/chrome_200_percent.pak",
            $"{framework}/Resources/gpu_shader_cache.bin",
            $"{framework}/Resources/icudtl.dat",
            $"{framework}/Resources/resources.pak",
            $"{framework}/Resources/en.lproj/locale.pak",
            $"{framework}/Resources/v8_context_snapshot.{snapshotArchitecture}.bin");
        RequireExecutable(paths["libexclr8cef.dylib"]);
        RequireExecutable(paths[$"{framework}/Chromium Embedded Framework"]);
        RequireExecutable(paths[$"{framework}/Libraries/libEGL.dylib"]);
        RequireExecutable(paths[$"{framework}/Libraries/libGLESv2.dylib"]);
        RequireExecutable(paths[$"{framework}/Libraries/libcef_sandbox.dylib"]);
        RequireExecutable(paths[$"{framework}/Libraries/libvk_swiftshader.dylib"]);
        ValidateMachO(paths["libexclr8cef.dylib"], rid);
        ValidateMachO(paths[$"{framework}/Chromium Embedded Framework"], rid);
        ValidateMachO(paths[$"{framework}/Libraries/libEGL.dylib"], rid);
        ValidateMachO(paths[$"{framework}/Libraries/libGLESv2.dylib"], rid);
        ValidateMachO(paths[$"{framework}/Libraries/libcef_sandbox.dylib"], rid);
        ValidateMachO(paths[$"{framework}/Libraries/libvk_swiftshader.dylib"], rid);

        var expectedHelpers = MacHelperSuffixes
            .Select(suffix => $"Asura Helper{suffix}.app")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualHelpers = paths.Keys
            .Select(path => path.Split('/', 2)[0])
            .Where(path => path.EndsWith(".app", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actualHelpers.SequenceEqual(expectedHelpers, StringComparer.Ordinal))
        {
            var missing = expectedHelpers.Except(
                actualHelpers,
                StringComparer.Ordinal);
            var unexpected = actualHelpers.Except(
                expectedHelpers,
                StringComparer.Ordinal);
            throw new InvalidDataException(
                "The CEF macOS runtime must contain exactly the five reviewed helper bundles. "
                + $"Missing: {string.Join(", ", missing)}. "
                + $"Unexpected: {string.Join(", ", unexpected)}.");
        }

        foreach (var suffix in MacHelperSuffixes)
        {
            var helperName = $"Asura Helper{suffix}";
            var bundle = $"{helperName}.app/Contents";
            var executable = RequireFile(paths, $"{bundle}/MacOS/{helperName}");
            RequireExecutable(executable);
            ValidateMachO(executable, rid);
            var plist = RequireFile(paths, $"{bundle}/Info.plist");
            ValidateHelperPropertyList(plist.FullPath, helperName, suffix);
        }
    }

    private static void ValidateHelperPropertyList(
        string path,
        string helperName,
        string suffix)
    {
        XDocument document;
        try
        {
            using var stream = RegularPackageFileReader.Open(path, out _);
            using var reader = XmlReader.Create(
                stream,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    MaxCharactersInDocument = 1_000_000,
                    XmlResolver = null,
                });
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                $"CEF helper {helperName} has a malformed Info.plist.",
                exception);
        }

        var rootValues = document.Root?.Elements().ToArray();
        if (!string.Equals(document.Root?.Name.LocalName, "plist"
, StringComparison.Ordinal) || rootValues is not { Length: 1 }
            || !string.Equals(rootValues[0].Name.LocalName, "dict", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CEF helper {helperName} has no single Info.plist dictionary.");
        }

        var dictionary = rootValues[0];
        var elements = dictionary.Elements().ToArray();
        if (elements.Length % 2 != 0)
        {
            throw new InvalidDataException(
                $"CEF helper {helperName} has an incomplete Info.plist pair.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < elements.Length; index += 2)
        {
            if (!string.Equals(elements[index].Name.LocalName, "key", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"CEF helper {helperName} has an invalid Info.plist shape.");
            }

            if (!values.TryAdd(
                    elements[index].Value,
                    elements[index + 1].Value))
            {
                throw new InvalidDataException(
                    $"CEF helper {helperName} repeats an Info.plist key.");
            }
        }

        var identifierSuffix = suffix switch
        {
            "" => string.Empty,
            " (Alerts)" => ".alerts",
            " (GPU)" => ".gpu",
            " (Plugin)" => ".plugin",
            " (Renderer)" => ".renderer",
            _ => throw new InvalidOperationException("Unexpected helper suffix."),
        };
        if (!string.Equals(values.GetValueOrDefault("CFBundleDisplayName"), helperName
, StringComparison.Ordinal) || !string.Equals(values.GetValueOrDefault("CFBundleExecutable"), helperName
, StringComparison.Ordinal) || !string.Equals(values.GetValueOrDefault("CFBundleName"), helperName
, StringComparison.Ordinal) || !string.Equals(values.GetValueOrDefault("CFBundleIdentifier")
, $"sh.asura.helper{identifierSuffix}"
, StringComparison.Ordinal) || !string.Equals(values.GetValueOrDefault("CFBundlePackageType"), "APPL", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CEF helper {helperName} has mismatched bundle identity.");
        }
    }

    private static void ValidateReceiptFiles(
        IReadOnlyList<ReceiptFile?> receiptFiles,
        IReadOnlyList<CefRuntimeFile> actualFiles)
    {
        if (receiptFiles.Count != actualFiles.Count)
        {
            throw new InvalidDataException(
                "The CEF runtime file closure does not match its receipt.");
        }

        for (var index = 0; index < actualFiles.Count; index++)
        {
            var expected = receiptFiles[index];
            var actual = actualFiles[index];
            if (expected is null)
            {
                throw new InvalidDataException(
                    "The CEF runtime receipt contains a null file entry.");
            }

            if (!string.Equals(expected.Path, actual.RelativePath
, StringComparison.Ordinal) || expected.Length != actual.Length
                || !string.Equals(expected.Sha256, actual.Sha256
, StringComparison.Ordinal) || expected.UnixMode != (int?)actual.UnixMode)
            {
                throw new InvalidDataException(
                    $"CEF runtime file {actual.RelativePath} does not match its receipt.");
            }
        }
    }

    private static byte[] WriteReceipt(
        CefRuntimeCatalog catalog,
        CefRuntimeDistribution distribution,
        string archiveSha256,
        string patchSetSha256,
        string sourceSnapshotSha256,
        IReadOnlyList<CefRuntimeFile> files)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("rid", distribution.Rid);
            writer.WriteString("platform", distribution.Platform);
            writer.WriteString("cefVersion", catalog.CefVersion);
            writer.WriteString("bindingRepository", catalog.BindingRepository);
            writer.WriteString("bindingCommit", catalog.BindingCommit);
            writer.WriteString("bindingVersion", catalog.BindingVersion);
            writer.WriteString("patchSetSha256", patchSetSha256);
            writer.WriteString(
                "bindingSourceSnapshotSha256",
                sourceSnapshotSha256);
            writer.WriteString("archiveSha1", distribution.ArchiveSha1);
            writer.WriteString("archiveSha256", archiveSha256);
            writer.WriteString("catalogSha256", Sha256(catalog.Content));
            writer.WriteBoolean("buildSucceeded", true);
            writer.WriteStartArray("files");
            foreach (var file in files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.RelativePath);
                writer.WriteNumber("length", file.Length);
                writer.WriteString("sha256", file.Sha256);
                if (file.UnixMode is { } unixMode)
                {
                    writer.WriteNumber("unixMode", (int)unixMode);
                }
                else
                {
                    writer.WriteNull("unixMode");
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return [.. buffer.ToArray(), (byte)'\n'];
    }

    private static CefRuntimeDistribution RequireDistribution(
        CefRuntimeCatalog catalog,
        string rid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        return catalog.Distributions.TryGetValue(rid, out var distribution)
            ? distribution
            : throw new InvalidDataException(
                $"RID {rid} is not present in the reviewed CEF runtime catalog.");
    }

    private static CefRuntimeFile RequireFile(
        IReadOnlyDictionary<string, CefRuntimeFile> paths,
        string path) =>
        paths.TryGetValue(path, out var file)
            ? file
            : throw new InvalidDataException(
                $"The CEF runtime is missing required file {path}.");

    private static void RequireFiles(
        IReadOnlyDictionary<string, CefRuntimeFile> paths,
        params string[] requiredPaths)
    {
        foreach (var path in requiredPaths)
        {
            RequireFile(paths, path);
        }
    }

    private static void RequireHash(
        IReadOnlyDictionary<string, CefRuntimeFile> paths,
        string path,
        string expectedHash)
    {
        var file = RequireFile(paths, path);
        if (!string.Equals(file.Sha256, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"CEF runtime file {path} does not match the reviewed digest.");
        }
    }

    private static void RequireExecutable(CefRuntimeFile file)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const UnixFileMode executeBits =
            UnixFileMode.UserExecute
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherExecute;
        if ((file.UnixMode & executeBits) == 0)
        {
            throw new InvalidDataException(
                $"CEF runtime file {file.RelativePath} lacks an execute bit.");
        }
    }

    private static void ValidateMachO(CefRuntimeFile file, string rid)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = RegularPackageFileReader.Open(file.FullPath, out _);
        if (stream.Length < header.Length)
        {
            throw WrongArchitecture(file);
        }

        stream.ReadExactly(header);

        int cpuType;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xfeedfacf)
        {
            cpuType = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        }
        else if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0xfeedfacf)
        {
            cpuType = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
        }
        else
        {
            throw WrongArchitecture(file);
        }

        var expectedCpuType = rid switch
        {
            "osx-arm64" => 0x0100000c,
            "osx-x64" => 0x01000007,
            _ => throw new InvalidOperationException(
                "Unexpected macOS CEF runtime identifier."),
        };
        if (cpuType != expectedCpuType)
        {
            throw WrongArchitecture(file);
        }
    }

    private static void ValidateElf(CefRuntimeFile file, string rid)
    {
        Span<byte> header = stackalloc byte[20];
        using var stream = RegularPackageFileReader.Open(file.FullPath, out _);
        if (stream.Length < header.Length)
        {
            throw WrongArchitecture(file);
        }

        stream.ReadExactly(header);
        if (header[0] != 0x7f
            || header[1] != (byte)'E'
            || header[2] != (byte)'L'
            || header[3] != (byte)'F'
            || header[4] != 2
            || header[5] != 1)
        {
            throw WrongArchitecture(file);
        }

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        var expectedMachine = rid switch
        {
            "linux-arm64" => (ushort)183,
            "linux-x64" => (ushort)62,
            _ => throw new InvalidOperationException(
                "Unexpected Linux CEF runtime identifier."),
        };
        if (machine != expectedMachine)
        {
            throw WrongArchitecture(file);
        }
    }

    private static void ValidatePeX64(CefRuntimeFile file)
    {
        Span<byte> dosHeader = stackalloc byte[64];
        using var stream = RegularPackageFileReader.Open(file.FullPath, out _);
        if (stream.Length < dosHeader.Length)
        {
            throw WrongArchitecture(file);
        }

        stream.ReadExactly(dosHeader);
        if (dosHeader[0] != (byte)'M'
            || dosHeader[1] != (byte)'Z')
        {
            throw WrongArchitecture(file);
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3c..]);
        Span<byte> peHeader = stackalloc byte[6];
        if (peOffset < dosHeader.Length
            || peOffset > stream.Length - peHeader.Length)
        {
            throw WrongArchitecture(file);
        }

        stream.Position = peOffset;
        stream.ReadExactly(peHeader);
        if (BinaryPrimitives.ReadUInt32LittleEndian(peHeader) != 0x00004550
            || BinaryPrimitives.ReadUInt16LittleEndian(peHeader[4..]) != 0x8664)
        {
            throw WrongArchitecture(file);
        }
    }

    private static InvalidDataException WrongArchitecture(CefRuntimeFile file) =>
        new($"CEF runtime file {file.RelativePath} has the wrong binary architecture.");

    private static byte[] ReadBoundedFile(
        string path,
        int maximumBytes,
        string description)
    {
        using var stream = RegularPackageFileReader.Open(path, out var file);
        if (file.Length is <= 0 || file.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The {description} is outside the allowed size range.");
        }

        var content = new byte[(int)file.Length];
        stream.ReadExactly(content);
        return content;
    }

    private static string Sha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException(
                        $"Duplicate JSON property {property.Name}.");
                }

                ValidateNoDuplicateProperties(property.Value);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            ValidateNoDuplicateProperties(item);
        }
    }

    private sealed class ReceiptDocument
    {
        public required int SchemaVersion { get; init; }

        public required string Rid { get; init; }

        public required string Platform { get; init; }

        public required string CefVersion { get; init; }

        public required string BindingRepository { get; init; }

        public required string BindingCommit { get; init; }

        public required string BindingVersion { get; init; }

        public required string PatchSetSha256 { get; init; }

        public required string BindingSourceSnapshotSha256 { get; init; }

        public required string ArchiveSha1 { get; init; }

        public required string ArchiveSha256 { get; init; }

        public required string CatalogSha256 { get; init; }

        public required bool BuildSucceeded { get; init; }

        public required List<ReceiptFile?>? Files { get; init; }
    }

    private sealed record ReceiptFile(
        string Path,
        long Length,
        string Sha256,
        int? UnixMode);
}

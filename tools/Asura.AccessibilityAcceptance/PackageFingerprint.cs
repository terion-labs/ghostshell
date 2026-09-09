using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace Asura.AccessibilityAcceptance;

internal sealed record PackageInspection(
    string ExecutablePath,
    string PackageRoot,
    BuildIdentity Build);

internal static class PackageFingerprint
{
    private const int MaximumPackageFiles = 20_000;
    private const int MaximumPackageEntries = MaximumPackageFiles * 2;
    private const long MaximumPackageBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumPackageDepth = 64;
    private const int MaximumPropertyListBytes = 1_000_000;
    private const int PropertyListProbeTimeoutMilliseconds = 5_000;
    private const int UnixStatBufferSize = 512;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFileType = 0x8000;

    public static PackageInspection Inspect(
        string packagePath,
        TargetPlatform platform,
        string buildLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildLabel);

        var (root, executable, packageKind, applicationIdentity, productVersion) =
            platform switch
            {
                TargetPlatform.MacOS => ResolveMacPackage(packagePath),
                TargetPlatform.Windows => ResolveFlatPackage(
                    packagePath,
                    "Asura.exe",
                    "windows-package",
                    "Asura.exe",
                    StringComparison.OrdinalIgnoreCase),
                TargetPlatform.LinuxX11 => ResolveFlatPackage(
                    packagePath,
                    "Asura",
                    "linux-x11-package",
                    "Asura",
                    StringComparison.Ordinal),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null),
            };

        EnsurePackageRoot(packagePath: root);
        EnsureExecutable(executable, platform);
        var executableFingerprint = HashRegularFile(executable, MaximumPackageBytes);
        var (fileCount, manifestSha256) = HashPackage(
            root,
            allowVelopackMetadataLink: platform == TargetPlatform.MacOS);
        var build = new BuildIdentity(
            buildLabel,
            packageKind,
            Path.GetFileName(executable),
            NormalizeVersion(productVersion),
            executableFingerprint.Length,
            executableFingerprint.Digest,
            fileCount,
            manifestSha256,
            applicationIdentity);
        return new PackageInspection(executable, root, build);
    }

    private static (
        string Root,
        string Executable,
        string PackageKind,
        string ApplicationIdentity,
        string ProductVersion) ResolveMacPackage(string packagePath)
    {
        var root = Path.GetFullPath(packagePath);
        if (!Directory.Exists(root)
            || !string.Equals(Path.GetFileName(root), "Asura.app", StringComparison.Ordinal))
        {
            throw new DirectoryNotFoundException(
                "The macOS package must be a directory named Asura.app.");
        }

        var infoPlist = Path.Combine(root, "Contents", "Info.plist");
        var values = ReadPropertyList(infoPlist);
        if (!values.TryGetValue("CFBundleIdentifier", out var bundleIdentifier)
            || !string.Equals(bundleIdentifier, "sh.asura", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Asura.app must declare bundle identifier sh.asura.");
        }

        if (!values.TryGetValue("CFBundleExecutable", out var executableName)
            || !string.Equals(executableName, "Asura", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Asura.app must declare Asura as CFBundleExecutable.");
        }

        if (!values.TryGetValue("CFBundleDisplayName", out var displayName)
            || !string.Equals(displayName, "Asura", StringComparison.Ordinal)
            || !values.TryGetValue("CFBundleName", out var bundleName)
            || !string.Equals(bundleName, "Asura", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Asura.app must declare Asura as its display and bundle name.");
        }

        if (!values.TryGetValue("CFBundleIconName", out var iconName)
            || !string.Equals(iconName, "Asura", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Asura.app must declare Asura as CFBundleIconName.");
        }

        var executable = Path.Combine(root, "Contents", "MacOS", executableName);
        var productVersion = values.GetValueOrDefault("CFBundleShortVersionString")
            ?? values.GetValueOrDefault("CFBundleVersion")
            ?? "unversioned";
        return (root, executable, "macos-application-bundle", bundleIdentifier, productVersion);
    }

    private static (
        string Root,
        string Executable,
        string PackageKind,
        string ApplicationIdentity,
        string ProductVersion) ResolveFlatPackage(
        string packagePath,
        string executableName,
        string packageKind,
        string applicationIdentity,
        StringComparison executableNameComparison)
    {
        var fullPath = Path.GetFullPath(packagePath);
        string root;
        string executable;
        if (File.Exists(fullPath))
        {
            if (!string.Equals(
                    Path.GetFileName(fullPath),
                    executableName,
                    executableNameComparison))
            {
                throw new FileNotFoundException(
                    $"The package executable must be named {executableName}.",
                    fullPath);
            }

            executable = fullPath;
            root = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The package executable has no parent directory.");
        }
        else
        {
            root = fullPath;
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException("The package path does not exist.");
            }

            executable = Path.Combine(root, executableName);
        }

        if (!File.Exists(executable))
        {
            throw new FileNotFoundException(
                $"The package does not contain {executableName}.",
                executable);
        }

        return (root, executable, packageKind, applicationIdentity, "unversioned");
    }

    private static Dictionary<string, string> ReadPropertyList(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Asura.app does not contain Contents/Info.plist.", path);
        }

        using var stream = OperatingSystem.IsWindows()
            ? File.OpenRead(path)
            : OpenUnixRegularFile(path, out _);
        if (stream.Length > MaximumPropertyListBytes)
        {
            throw new InvalidDataException(
                $"Asura.app Info.plist exceeds {MaximumPropertyListBytes} bytes.");
        }

        Span<byte> magic = stackalloc byte[8];
        var magicLength = stream.Read(magic);
        stream.Position = 0;
        if (magicLength == magic.Length
            && magic.SequenceEqual("bplist00"u8))
        {
            return ReadBinaryPropertyList(path);
        }

        return ReadXmlPropertyList(stream);
    }

    private static Dictionary<string, string> ReadXmlPropertyList(Stream stream)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
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
                "Asura.app Info.plist must be an inspectable XML property list.",
                exception);
        }

        var root = document.Root;
        if (root is null
            || !string.Equals(root.Name.LocalName, "plist", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Info.plist does not contain a plist root element.");
        }

        var rootValues = root.Elements().ToArray();
        if (rootValues.Length != 1 || !string.Equals(rootValues[0].Name.LocalName, "dict", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Info.plist does not contain one root dictionary.");
        }

        var dictionary = rootValues[0];
        var children = dictionary.Elements().ToArray();
        if (children.Length % 2 != 0)
        {
            throw new InvalidDataException("Info.plist contains an incomplete dictionary pair.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < children.Length; index += 2)
        {
            if (!string.Equals(children[index].Name.LocalName, "key", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Info.plist dictionary values must be preceded by a key element.");
            }

            var key = children[index].Value;
            if (!keys.Add(key))
            {
                throw new InvalidDataException("Info.plist contains a duplicate key.");
            }

            if (string.Equals(children[index + 1].Name.LocalName, "string", StringComparison.Ordinal))
            {
                result.Add(key, children[index + 1].Value);
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadBinaryPropertyList(string path)
    {
        const string plutil = "/usr/bin/plutil";
        if (!OperatingSystem.IsMacOS() || !File.Exists(plutil))
        {
            throw new InvalidDataException(
                "Binary Info.plist identity inspection requires the native macOS plutil utility.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in new[]
                 {
                     "CFBundleIdentifier",
                     "CFBundleExecutable",
                     "CFBundleDisplayName",
                     "CFBundleName",
                     "CFBundleIconName",
                     "CFBundleShortVersionString",
                     "CFBundleVersion",
                 })
        {
            var value = ExtractBinaryPropertyListValue(plutil, path, key);
            if (value is not null)
            {
                result.Add(key, value);
            }
        }

        return result;
    }

    private static string? ExtractBinaryPropertyListValue(
        string plutil,
        string path,
        string key)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plutil,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-extract", key, "raw", "-o", "-", path })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidDataException("Info.plist identity inspection could not start.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(PropertyListProbeTimeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(milliseconds: 1_000);
                throw new InvalidDataException("Info.plist identity inspection timed out.");
            }

            _ = standardError.GetAwaiter().GetResult();
            var output = standardOutput.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return null;
            }

            if (output.Length > 4_096)
            {
                throw new InvalidDataException("Info.plist identity value exceeds the inspection limit.");
            }

            return output.TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException("Info.plist identity inspection failed.", exception);
        }
    }

    private static void EnsureExecutable(string path, TargetPlatform platform)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The package executable does not exist.", path);
        }

        if (platform == TargetPlatform.Windows || OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        const UnixFileMode executeBits =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((mode & executeBits) == UnixFileMode.None)
        {
            throw new InvalidDataException("The package executable lacks an execute bit.");
        }
    }

    private static void EnsurePackageRoot(string packagePath)
    {
        var root = new DirectoryInfo(packagePath);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException("The package root does not exist.");
        }

        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The acceptance package root is a symbolic link or reparse point.");
        }
    }

    private static (int FileCount, string Digest) HashPackage(
        string packageRoot,
        bool allowVelopackMetadataLink)
    {
        var entries = EnumeratePackageEntries(
                packageRoot,
                allowVelopackMetadataLink)
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var fileCount = entries.Count(entry => entry.Kind != PackageEntryKind.Directory);
        if (fileCount == 0)
        {
            throw new InvalidDataException(
                $"The package must contain between 1 and {MaximumPackageFiles} files.");
        }

        using var packageHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBuffer = stackalloc byte[sizeof(long)];
        Span<byte> metadataBuffer = stackalloc byte[sizeof(int)];
        long totalBytes = 0;
        foreach (var entry in entries)
        {
            packageHash.AppendData(entry.Kind switch
            {
                PackageEntryKind.Directory => [(byte)'D'],
                PackageEntryKind.RegularFile => [(byte)'F'],
                PackageEntryKind.SymbolicLink => [(byte)'L'],
                _ => throw new InvalidOperationException("Unsupported package entry kind."),
            });
            packageHash.AppendData(Encoding.UTF8.GetBytes(entry.RelativePath));
            packageHash.AppendData([0]);

            if (entry.Kind == PackageEntryKind.Directory)
            {
                BinaryPrimitives.WriteInt32BigEndian(
                    metadataBuffer,
                    GetRelevantDirectoryMetadata(entry.FullPath));
                packageHash.AppendData(metadataBuffer);
                continue;
            }

            if (entry.Kind == PackageEntryKind.SymbolicLink)
            {
                var target = entry.LinkTarget
                    ?? throw new InvalidOperationException("A symbolic-link entry has no target.");
                var targetBytes = Encoding.UTF8.GetBytes(target);
                totalBytes = checked(totalBytes + targetBytes.Length);
                EnsureLengthWithinFingerprintBoundary(totalBytes, MaximumPackageBytes);
                BinaryPrimitives.WriteInt64BigEndian(lengthBuffer, targetBytes.Length);
                packageHash.AppendData(lengthBuffer);
                packageHash.AppendData(targetBytes);
                continue;
            }

            var fingerprint = HashRegularFile(entry.FullPath, MaximumPackageBytes - totalBytes);
            BinaryPrimitives.WriteInt32BigEndian(metadataBuffer, fingerprint.Metadata);
            packageHash.AppendData(metadataBuffer);
            try
            {
                totalBytes = checked(totalBytes + fingerprint.Length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "The package byte count exceeded the fingerprint boundary.",
                    exception);
            }

            if (totalBytes > MaximumPackageBytes)
            {
                throw new InvalidDataException(
                    $"The package exceeds the {MaximumPackageBytes}-byte fingerprint limit.");
            }

            BinaryPrimitives.WriteInt64BigEndian(lengthBuffer, fingerprint.Length);
            packageHash.AppendData(lengthBuffer);
            packageHash.AppendData(Convert.FromHexString(fingerprint.Digest));
        }

        return (fileCount, Convert.ToHexString(packageHash.GetHashAndReset()).ToLowerInvariant());
    }

    private static IReadOnlyList<PackageEntry> EnumeratePackageEntries(
        string packageRoot,
        bool allowVelopackMetadataLink)
    {
        var root = new DirectoryInfo(Path.GetFullPath(packageRoot));
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The acceptance package root is a symbolic link or reparse point.");
        }

        var entries = new List<PackageEntry>
        {
            new(root.FullName, ".", PackageEntryKind.Directory, LinkTarget: null),
        };
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((root, 0));
        var entryCount = 0;
        var fileCount = 0;
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
                if (entryCount > MaximumPackageEntries)
                {
                    throw new InvalidDataException(
                        $"The package exceeds the {MaximumPackageEntries}-entry traversal limit.");
                }

                var relativePath = NormalizeRelativePath(root.FullName, entry.FullName);
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entries.Add(InspectSymbolicLink(
                        root.FullName,
                        entry,
                        relativePath,
                        allowVelopackMetadataLink));
                    fileCount++;
                    if (fileCount > MaximumPackageFiles)
                    {
                        throw new InvalidDataException(
                            $"The package exceeds the {MaximumPackageFiles}-file fingerprint limit.");
                    }

                    continue;
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    if (depth >= MaximumPackageDepth)
                    {
                        throw new InvalidDataException(
                            $"The package exceeds the {MaximumPackageDepth}-directory depth limit.");
                    }

                    entries.Add(new PackageEntry(
                        childDirectory.FullName,
                        relativePath,
                        PackageEntryKind.Directory,
                        LinkTarget: null));
                    pending.Push((childDirectory, depth + 1));
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    throw new InvalidDataException(
                        "The acceptance package contains an unsupported filesystem entry.");
                }

                entries.Add(new PackageEntry(
                    file.FullName,
                    relativePath,
                    PackageEntryKind.RegularFile,
                    LinkTarget: null));
                fileCount++;
                if (fileCount > MaximumPackageFiles)
                {
                    throw new InvalidDataException(
                        $"The package exceeds the {MaximumPackageFiles}-file fingerprint limit.");
                }
            }
        }

        return entries;
    }

    private static PackageEntry InspectSymbolicLink(
        string packageRoot,
        FileSystemInfo entry,
        string relativePath,
        bool allowVelopackMetadataLink)
    {
        const string metadataPath = "Contents/MacOS/sq.version";
        const string metadataTarget = "../Resources/sq.version";
        if (!allowVelopackMetadataLink
            || entry is not FileInfo
            || !string.Equals(relativePath, metadataPath, StringComparison.Ordinal)
            || !string.Equals(entry.LinkTarget, metadataTarget, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The acceptance package contains an unsupported symbolic link or reparse point.");
        }

        var resolvedTarget = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(entry.FullName)
                ?? throw new InvalidDataException("The Velopack metadata link has no parent directory."),
            metadataTarget));
        var expectedTarget = Path.Combine(
            packageRoot,
            "Contents",
            "Resources",
            "sq.version");
        if (!string.Equals(resolvedTarget, expectedTarget, StringComparison.Ordinal)
            || !File.Exists(resolvedTarget)
            || File.GetAttributes(resolvedTarget).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The Velopack metadata link must resolve to its regular in-bundle resource.");
        }

        return new PackageEntry(
            entry.FullName,
            relativePath,
            PackageEntryKind.SymbolicLink,
            metadataTarget);
    }

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static int GetRelevantDirectoryMetadata(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            const FileAttributes relevantAttributes =
                FileAttributes.ReadOnly
                | FileAttributes.Hidden
                | FileAttributes.System
                | FileAttributes.Archive
                | FileAttributes.Compressed
                | FileAttributes.Encrypted;
            return (int)(File.GetAttributes(path) & relevantAttributes);
        }

        return (int)File.GetUnixFileMode(path);
    }

    private static FileFingerprint HashRegularFile(string path, long maximumBytes)
    {
        if (maximumBytes < 0)
        {
            throw new InvalidDataException(
                $"The package exceeds the {MaximumPackageBytes}-byte fingerprint limit.");
        }

        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory
                    | FileAttributes.ReparsePoint
                    | FileAttributes.Device)) != FileAttributes.None)
            {
                throw new InvalidDataException(
                    "The acceptance package contains a non-regular file.");
            }

            using var windowsStream = File.OpenRead(path);
            EnsureLengthWithinFingerprintBoundary(windowsStream.Length, maximumBytes);
            return new FileFingerprint(
                windowsStream.Length,
                Convert.ToHexString(SHA256.HashData(windowsStream)).ToLowerInvariant(),
                GetRelevantWindowsMetadata(attributes));
        }

        using var unixStream = OpenUnixRegularFile(path, out var metadata);
        EnsureLengthWithinFingerprintBoundary(unixStream.Length, maximumBytes);
        return new FileFingerprint(
            unixStream.Length,
            Convert.ToHexString(SHA256.HashData(unixStream)).ToLowerInvariant(),
            metadata);
    }

    private static void EnsureLengthWithinFingerprintBoundary(long length, long maximumBytes)
    {
        if (length < 0 || length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The package exceeds the {MaximumPackageBytes}-byte fingerprint limit.");
        }
    }

    internal static FileStream OpenUnixRegularFile(string path, out int metadata)
    {
        metadata = 0;
        var flags = OperatingSystem.IsMacOS()
            ? 0x0004 | 0x00000100 | 0x01000000 // O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC
            : 0x0800 | 0x00020000 | 0x00080000;
        var descriptor = Open(path, flags);
        if (descriptor < 0)
        {
            throw new IOException(
                "The package file could not be opened without following a blocking stream.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        }

        SafeFileHandle? handle = new((nint)descriptor, ownsHandle: true);
        try
        {
            var statBuffer = Marshal.AllocHGlobal(UnixStatBufferSize);
            try
            {
                if (FStat(descriptor, statBuffer) != 0)
                {
                    throw new IOException(
                        "The package file type could not be inspected.",
                        new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
                }

                var mode = ReadUnixMode(statBuffer);
                if ((mode & UnixFileTypeMask) != UnixRegularFileType)
                {
                    throw new InvalidDataException(
                        "The acceptance package contains a non-regular file.");
                }

                metadata = mode & ~UnixFileTypeMask;
            }
            finally
            {
                Marshal.FreeHGlobal(statBuffer);
            }

            var stream = new FileStream(handle, FileAccess.Read);
            handle = null;
            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static int GetRelevantWindowsMetadata(FileAttributes attributes)
    {
        const FileAttributes relevantAttributes =
            FileAttributes.ReadOnly
            | FileAttributes.Hidden
            | FileAttributes.System
            | FileAttributes.Archive
            | FileAttributes.Compressed
            | FileAttributes.Encrypted;
        return (int)(attributes & relevantAttributes);
    }

    private static int ReadUnixMode(nint statBuffer)
    {
        var modeOffset = (OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture) switch
        {
            (true, Architecture.X64 or Architecture.Arm64) => 4,
            (false, Architecture.X64) => 24,
            (false, Architecture.Arm64) => 16,
            _ => throw new PlatformNotSupportedException(
                "Package acceptance supports x64 and arm64 Unix hosts."),
        };
        return Marshal.ReadInt32(statBuffer, modeOffset) & 0xFFFF;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fileDescriptor, nint statBuffer);

    private static string NormalizeVersion(string value)
    {
        var sanitized = EvidenceSanitizer.SanitizeSingleLine(value).Value;
        return EvidenceSanitizer.IsSafeVersionText(sanitized)
            ? sanitized
            : "unversioned";
    }

    private enum PackageEntryKind
    {
        RegularFile,
        Directory,
        SymbolicLink,
    }

    private sealed record PackageEntry(
        string FullPath,
        string RelativePath,
        PackageEntryKind Kind,
        string? LinkTarget);

    private sealed record FileFingerprint(long Length, string Digest, int Metadata);
}

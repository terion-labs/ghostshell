namespace Asura.Packaging;

internal sealed record NativeArtifactPublishCommand(
    string StagedDirectory,
    string DestinationDirectory,
    string? Component = null)
{
    private static readonly HashSet<string> Options =
    [
        "--destination",
        "--component",
        "--staged-directory",
    ];

    public static NativeArtifactPublishCommand Parse(
        IReadOnlyList<string> arguments)
    {
        var values = PackagingCommandParser.Parse(arguments, Options);
        return new NativeArtifactPublishCommand(
            PackagingCommandParser.Required(
                values,
                "--staged-directory"),
            PackagingCommandParser.Required(
                values,
                "--destination"),
            values.GetValueOrDefault("--component"));
    }
}

internal sealed record NativeArtifactPublishResult(
    string DestinationDirectory,
    bool ReplacedExistingDirectory);

/// <summary>
/// Publishes a completed native artifact tree from a private sibling directory.
/// New destinations use an exclusive rename; rebuilds atomically exchange the
/// staged and existing trees so a failed syscall cannot destroy the old build.
/// </summary>
internal static class NativeArtifactPublisher
{
    private static readonly HashSet<string> ArtifactDirectoryNames =
    [
        "common",
        "linux-arm64",
        "linux-x64",
        "osx-arm64",
        "osx-x64",
        "win-x64",
    ];
    private const string PrivateParentPrefix =
        ".asura-native-artifacts.";
    private const int MaximumEntries = 40_000;
    private const int MaximumDepth = 32;
    private static readonly HashSet<string> TerminalRoots = new(StringComparer.Ordinal)
    {
        "libghostty-vt.dylib",
        "libghostty-vt.so",
        "ghostty-vt.dll",
        "libasura_pty.dylib",
        "libasura_pty.so",
        "PORTA-PTY-LICENSE",
        "GHOSTTY-LICENSE",
        "ghostty-vt-required-exports.txt",
        "native-terminal-build-receipt.json",
        "ghostty",
    };

    public static NativeArtifactPublishResult Publish(
        string stagedDirectory,
        string destinationDirectory,
        string? component = null)
    {
        if (component is not null and not "terminal" and not "cef")
        {
            throw new ArgumentException("Unknown native artifact component.", nameof(component));
        }

        var staged = RequireExistingDirectoryWithoutLinks(
            stagedDirectory,
            nameof(stagedDirectory));
        var destination = ParseDestination(destinationDirectory);
        ValidatePrivateSibling(staged, destination.Path);
        using var publicationLock = AcquirePublicationLock(destination.Path);
        ValidateTree(staged, "staged native artifact");
        if (destination.Exists)
        {
            ValidateTree(destination.Path, "existing native artifact");
        }

        if (component is not null)
        {
            PreserveIndependentComponents(staged, destination, component);
            ValidateTree(staged, "merged native artifact");
        }

        // Re-run path validation immediately before the atomic operation. On
        // macOS, ExclusiveDirectoryMover also asks the kernel not to follow any
        // symlink in either rename path.
        _ = RequireExistingDirectoryWithoutLinks(
            staged,
            nameof(stagedDirectory));
        var currentDestination = ParseDestination(destination.Path);
        if (currentDestination.Exists != destination.Exists)
        {
            throw new IOException(
                "The native artifact destination changed during publication.");
        }

        if (destination.Exists)
        {
            ExclusiveDirectoryMover.Exchange(staged, destination.Path);
        }
        else
        {
            ExclusiveDirectoryMover.Move(staged, destination.Path);
        }

        return new NativeArtifactPublishResult(
            destination.Path,
            destination.Exists);
    }

    private static FileStream AcquirePublicationLock(string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        var path = Path.Combine(parent, $".asura-native-publication.{Path.GetFileName(destination)}.lock");
        if (InspectPathWithoutFollowing(path) == InspectedPath.Directory)
        {
            throw new IOException("The native publication lock is not a regular file.");
        }

        // Cooperating publishers fail rather than overwrite a sibling published
        // concurrently. The OS releases the exclusive handle even after a crash.
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static void PreserveIndependentComponents(string staged, DestinationState destination, string component)
    {
        bool Owns(string name) => string.Equals(component, "terminal", StringComparison.Ordinal)
            ? TerminalRoots.Contains(name)
            : string.Equals(name, "cef", StringComparison.Ordinal);
        if (Enumerate(new DirectoryInfo(staged), "component artifact")
            .Any(entry => !Owns(entry.Name)))
        {
            throw new InvalidDataException("Native component publication contains an independently owned component.");
        }

        if (!destination.Exists)
        {
            return;
        }

        // Siblings retain their own receipts; copying them does not add them to
        // the terminal receipt. Only the completed merged tree is exchanged.
        foreach (var entry in Enumerate(new DirectoryInfo(destination.Path), "existing native artifact"))
        {
            if (!Owns(entry.Name))
            {
                CopyIndependentEntry(entry, Path.Combine(staged, entry.Name));
            }
        }
    }

    private static void CopyIndependentEntry(FileSystemInfo entry, string destination)
    {
        RejectLink(entry);
        if (entry is DirectoryInfo directory)
        {
            Directory.CreateDirectory(destination);
            foreach (var child in Enumerate(directory, "independent native artifact"))
            {
                CopyIndependentEntry(child, Path.Combine(destination, child.Name));
            }

            return;
        }

        using var source = RegularPackageFileReader.Open(entry.FullName, out _);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(output);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, File.GetUnixFileMode(entry.FullName));
        }
    }

    private static DestinationState ParseDestination(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
        var artifactDirectoryName = Path.GetFileName(fullPath);
        if (!ArtifactDirectoryNames.Contains(artifactDirectoryName))
        {
            throw new ArgumentException(
                "The native artifact destination must use the common asset name "
                + "or a supported runtime identifier.",
                nameof(path));
        }

        var parent = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "The native artifact destination requires a parent directory.",
                nameof(path));
        _ = RequireExistingDirectoryWithoutLinks(
            parent,
            "native artifact destination parent");
        var target = InspectPathWithoutFollowing(fullPath);
        return target switch
        {
            InspectedPath.Missing => new DestinationState(fullPath, false),
            InspectedPath.Directory => new DestinationState(fullPath, true),
            _ => throw new IOException(
                "The native artifact destination exists but is not a directory."),
        };
    }

    private static void ValidatePrivateSibling(
        string staged,
        string destination)
    {
        if (!string.Equals(
                Path.GetFileName(staged),
                Path.GetFileName(destination),
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The staged and destination native artifact directories must use "
                + "the same artifact identifier.",
                nameof(staged));
        }

        var stagedParent = Path.GetDirectoryName(staged)
            ?? throw new ArgumentException(
                "The staged native artifact requires a private parent directory.",
                nameof(staged));
        var stagedParentOwner = Path.GetDirectoryName(stagedParent)
            ?? throw new ArgumentException(
                "The staged native artifact private parent requires an owner.",
                nameof(staged));
        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException(
                "The native artifact destination requires a parent directory.",
                nameof(destination));
        var privateName = Path.GetFileName(stagedParent);
        if (privateName.Length == PrivateParentPrefix.Length
            || !privateName.StartsWith(
                PrivateParentPrefix,
                StringComparison.Ordinal)
            || !MacOsPackagePaths.AreSameDirectory(
                stagedParentOwner,
                destinationParent))
        {
            throw new ArgumentException(
                "The staged native artifact must be inside a private sibling "
                + "directory owned by the destination parent.",
                nameof(staged));
        }
    }

    private static string RequireExistingDirectoryWithoutLinks(
        string path,
        string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, label);
        var fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException(
                $"The {label} path has no filesystem root.");
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        if (InspectPathWithoutFollowing(current) != InspectedPath.Directory)
        {
            throw new DirectoryNotFoundException(
                $"The {label} filesystem root does not exist.");
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (InspectPathWithoutFollowing(current) != InspectedPath.Directory)
            {
                throw new DirectoryNotFoundException(
                    $"The {label} directory or one of its ancestors does not exist.");
            }
        }

        return fullPath;
    }

    private static InspectedPath InspectPathWithoutFollowing(string path)
    {
        var directory = new DirectoryInfo(path);
        directory.Refresh();
        RejectLink(directory);
        if (directory.Exists)
        {
            return InspectedPath.Directory;
        }

        var file = new FileInfo(path);
        file.Refresh();
        RejectLink(file);
        return file.Exists
            ? InspectedPath.Other
            : InspectedPath.Missing;
    }

    private static void ValidateTree(string rootPath, string label)
    {
        var root = new DirectoryInfo(rootPath);
        RequireSafeDirectory(root, label);
        var paths = new List<string>();
        var pending = new Stack<(DirectoryInfo Directory, string Prefix, int Depth)>();
        pending.Push((root, string.Empty, 0));
        var entryCount = 0;

        while (pending.Count > 0)
        {
            var (directory, prefix, depth) = pending.Pop();
            var entries = Enumerate(directory, label);
            foreach (var entry in entries)
            {
                entryCount++;
                if (entryCount > MaximumEntries)
                {
                    throw new InvalidDataException(
                        $"The {label} tree exceeds the entry limit.");
                }

                RejectLink(entry);
                var relativePath = string.IsNullOrEmpty(prefix)
                    ? entry.Name
                    : $"{prefix}/{entry.Name}";
                NativeArtifactPath.Validate(relativePath);
                paths.Add(relativePath);
                var entryDepth = depth + 1;
                if (entryDepth > MaximumDepth)
                {
                    throw new InvalidDataException(
                        $"The {label} tree exceeds the depth limit.");
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push((childDirectory, relativePath, entryDepth));
                    continue;
                }

                if (entry is not FileInfo)
                {
                    throw new InvalidDataException(
                        $"The {label} tree contains an unsupported entry.");
                }

                using var stream = RegularPackageFileReader.Open(
                    entry.FullName,
                    out _);
            }
        }

        NativeArtifactPath.ValidatePortableUniqueness(paths);
        RequireSafeDirectory(root, label);
    }

    private static FileSystemInfo[] Enumerate(
        DirectoryInfo directory,
        string label)
    {
        RequireSafeDirectory(directory, label);
        var entries = directory.EnumerateFileSystemInfos(
                "*",
                new EnumerationOptions
                {
                    AttributesToSkip = FileAttributes.None,
                    IgnoreInaccessible = false,
                    RecurseSubdirectories = false,
                    ReturnSpecialDirectories = false,
                })
            .OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        RequireSafeDirectory(directory, label);
        return entries;
    }

    private static void RequireSafeDirectory(
        DirectoryInfo directory,
        string label)
    {
        directory.Refresh();
        RejectLink(directory);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException(
                $"The {label} directory disappeared during publication.");
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null
            || (entry.Exists
                && entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new InvalidDataException(
                "Native artifact publication refuses symbolic links "
                + "and reparse points.");
        }
    }

    private enum InspectedPath
    {
        Missing,
        Directory,
        Other,
    }

    private sealed record DestinationState(string Path, bool Exists);
}

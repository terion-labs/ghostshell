using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asura.SecurityCampaign;

internal sealed record ReleaseSourceSealVerification(
    ReleaseSourceSealDocument Seal,
    string SealSha256,
    string ObservedManifestSha256);

/// <summary>
/// Binds a release build to the byte-for-byte tagged export used by the
/// compiler. Generated files are allowed only below this closed set of roots.
/// </summary>
internal static class ReleaseSourceSeal
{
    internal const string SealFileName = "source-seal.json";
    internal const string SealChecksumFileName = "source-seal.json.sha256";
    internal const string BuildIdentityFileName = "release-build-identity.json";
    internal const string SealSchemaRelativePath =
        "scripts/acceptance/security-campaign/source-seal.schema.json";
    private const string RepositoryIdentity = "https://github.com/terion-labs/asura";
    private const string RootFinderMetadataPath = ".DS_Store";
    private const int MaximumManifestEntries = 20_000;
    private const long MaximumSealBytes = 16L * 1024 * 1024;

    private static readonly string[] GeneratedRoots =
    [
        ".deps",
        "native/artifacts",
        "native/sql-language-worker/target",
    ];

    public static ReleaseSourceSealVerification Create(
        string repository,
        string sourceRoot,
        string expectedCommit,
        string expectedTree,
        string tag,
        string outputDirectory)
    {
        var gitRoot = Path.GetFullPath(repository);
        var exportRoot = Path.GetFullPath(sourceRoot);
        var output = Path.GetFullPath(outputDirectory);
        RequireExternalPath(output, gitRoot, exportRoot);
        RequireCanonicalRepository(gitRoot);
        RequireTag(tag);
        RequireHex(expectedCommit, 40, "source commit");
        RequireHex(expectedTree, 40, "source tree");
        RequireExactHead(gitRoot, expectedCommit, expectedTree);
        RequireTagTarget(gitRoot, tag, expectedCommit);
        RequireCleanTrackedState(gitRoot);

        var archive = ReadTaggedArchive(gitRoot, expectedCommit);
        var ignoreRootFinderMetadata = !ContainsPath(archive.Files, RootFinderMetadataPath);
        var exported = ReadDirectoryManifest(exportRoot, [], ignoreRootFinderMetadata);
        RequireSameManifest(archive.Files, exported.Files, "The exported release source differs from the exact tagged archive.");
        if (archive.Files.Any(entry => IsGeneratedPath(entry.RelativePath)))
        {
            throw new InvalidDataException("A generated release root unexpectedly contains tagged source files.");
        }

        var schemaSha256 = CampaignFiles.Sha256File(
            Path.Combine(exportRoot, SealSchemaRelativePath),
            1024 * 1024);
        var seal = new ReleaseSourceSealDocument(
            1,
            "asura-release-source-seal-v1",
            RepositoryIdentity,
            tag,
            expectedCommit,
            expectedTree,
            archive.ArchiveSha256,
            schemaSha256,
            archive.ManifestSha256,
            archive.Files,
            GeneratedRoots);
        WriteSeal(output, seal);
        return Verify(
            exportRoot,
            output,
            expectedCommit,
            expectedTree,
            tag,
            buildIdentityOutput: null);
    }

    public static ReleaseSourceSealVerification Verify(
        string sourceRoot,
        string sealDirectory,
        string expectedCommit,
        string expectedTree,
        string tag,
        string? buildIdentityOutput)
    {
        var root = Path.GetFullPath(sourceRoot);
        var sealRoot = Path.GetFullPath(sealDirectory);
        RequireExternalPath(sealRoot, root);
        var sealPath = Path.Combine(sealRoot, SealFileName);
        var sealBytes = ReadSealBytes(sealRoot, sealPath);
        var sealSha256 = CampaignFiles.Sha256(sealBytes);
        var seal = CampaignFiles.ReadJson<ReleaseSourceSealDocument>(sealPath);
        ValidateSeal(seal, root, expectedCommit, expectedTree, tag);

        var observed = ReadDirectoryManifest(
            root,
            seal.GeneratedRoots,
            !ContainsPath(seal.Files, RootFinderMetadataPath));
        var status = string.Equals(
            observed.ManifestSha256,
            seal.ManifestSha256,
            StringComparison.Ordinal)
            && ManifestsEqual(seal.Files, observed.Files)
                ? "pass"
                : "mismatch";
        if (buildIdentityOutput is not null)
        {
            WriteBuildIdentity(
                buildIdentityOutput,
                new ReleaseBuildIdentityDocument(
                    1,
                    "asura-release-build-identity-v1",
                    sealSha256,
                    seal.ManifestSha256,
                    observed.ManifestSha256,
                    status));
        }

        if (!string.Equals(status, "pass", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The release build source no longer matches its sealed tagged manifest.");
        }

        return new ReleaseSourceSealVerification(seal, sealSha256, observed.ManifestSha256);
    }

    public static ReleaseBuildIdentityDocument ValidateBuildIdentity(
        string path,
        ReleaseSourceSealVerification verification)
    {
        var identity = CampaignFiles.ReadJson<ReleaseBuildIdentityDocument>(path);
        if (identity.SchemaVersion != 1
            || !string.Equals(identity.Format, "asura-release-build-identity-v1", StringComparison.Ordinal)
            || !string.Equals(identity.Status, "pass", StringComparison.Ordinal)
            || !string.Equals(identity.SourceSealSha256, verification.SealSha256, StringComparison.Ordinal)
            || !string.Equals(identity.SealedManifestSha256, verification.Seal.ManifestSha256, StringComparison.Ordinal)
            || !string.Equals(identity.ObservedManifestSha256, verification.ObservedManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The candidate build identity does not match the sealed release source.");
        }

        return identity;
    }

    private static void ValidateSeal(
        ReleaseSourceSealDocument seal,
        string sourceRoot,
        string expectedCommit,
        string expectedTree,
        string tag)
    {
        if (seal.SchemaVersion != 1
            || !string.Equals(seal.Format, "asura-release-source-seal-v1", StringComparison.Ordinal)
            || !string.Equals(seal.Repository, RepositoryIdentity, StringComparison.Ordinal)
            || !string.Equals(seal.Tag, tag, StringComparison.Ordinal)
            || !string.Equals(seal.Commit, expectedCommit, StringComparison.Ordinal)
            || !string.Equals(seal.Tree, expectedTree, StringComparison.Ordinal)
            || !seal.GeneratedRoots.SequenceEqual(GeneratedRoots, StringComparer.Ordinal)
            || seal.Files.Count is < 1 or > MaximumManifestEntries
            || !string.Equals(
                seal.SealSchemaSha256,
                CampaignFiles.Sha256File(Path.Combine(sourceRoot, SealSchemaRelativePath), 1024 * 1024),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The release source seal identity is invalid.");
        }

        RequireTag(seal.Tag);
        RequireHex(seal.Commit, 40, "sealed source commit");
        RequireHex(seal.Tree, 40, "sealed source tree");
        RequireHex(seal.SourceArchiveSha256, 64, "sealed source archive SHA-256");
        RequireHex(seal.SealSchemaSha256, 64, "source seal schema SHA-256");
        RequireHex(seal.ManifestSha256, 64, "sealed source manifest SHA-256");
        ValidateManifestEntries(seal.Files);
        if (!string.Equals(ManifestDigest(seal.Files), seal.ManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The source seal manifest digest is invalid.");
        }
    }

    private static byte[] ReadSealBytes(string sealRoot, string sealPath)
    {
        var expected = new HashSet<string>(
            [SealFileName, SealChecksumFileName],
            StringComparer.Ordinal);
        var actual = Directory.EnumerateFileSystemEntries(sealRoot)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException("The source seal directory must contain exactly the seal and checksum files.");
        }

        var bytes = CampaignFiles.ReadFile(sealPath, MaximumSealBytes);
        var expectedChecksum = CampaignFiles.Sha256(bytes) + "  " + SealFileName + "\n";
        if (!string.Equals(
                File.ReadAllText(Path.Combine(sealRoot, SealChecksumFileName), Encoding.UTF8),
                expectedChecksum,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The source seal checksum does not match its document.");
        }

        return bytes;
    }

    private static void WriteSeal(string output, ReleaseSourceSealDocument seal)
    {
        if (Directory.Exists(output) || File.Exists(output))
        {
            throw new IOException("The source seal output must not already exist.");
        }

        Directory.CreateDirectory(output);
        try
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(seal, CampaignFiles.StrictJson);
            File.WriteAllBytes(Path.Combine(output, SealFileName), content);
            File.WriteAllText(
                Path.Combine(output, SealChecksumFileName),
                CampaignFiles.Sha256(content) + "  " + SealFileName + "\n",
                new UTF8Encoding(false));
        }
        catch
        {
            Directory.Delete(output, recursive: true);
            throw;
        }
    }

    private static void WriteBuildIdentity(string outputPath, ReleaseBuildIdentityDocument identity)
    {
        var path = Path.GetFullPath(outputPath);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("The release build identity output must not already exist.");
        }

        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The release build identity has no parent directory.");
        Directory.CreateDirectory(parent);
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(identity, CampaignFiles.StrictJson));
    }

    private static TaggedArchive ReadTaggedArchive(string repository, string commit)
    {
        var archivePath = Path.Combine(
            Path.GetTempPath(),
            $"asura-source-seal-{Guid.NewGuid():N}.tar");
        try
        {
            WriteGitArchive(repository, commit, archivePath);
            var archiveSha256 = CampaignFiles.Sha256File(archivePath);
            var files = new List<ReleaseSourceManifestEntry>();
            using var stream = File.OpenRead(archivePath);
            using var reader = new TarReader(stream, leaveOpen: false);
            TarEntry? entry;
            while ((entry = reader.GetNextEntry(copyData: false)) is not null)
            {
                if (entry.EntryType is TarEntryType.Directory
                    or TarEntryType.ExtendedAttributes
                    or TarEntryType.GlobalExtendedAttributes)
                {
                    continue;
                }

                if (entry.EntryType is not (TarEntryType.RegularFile
                    or TarEntryType.V7RegularFile
                    or TarEntryType.ContiguousFile))
                {
                    throw new InvalidDataException($"The tagged source contains unsupported entry {entry.Name}.");
                }

                var path = RequireRelativePath(entry.Name);
                var data = entry.DataStream ?? Stream.Null;
                var sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
                files.Add(new ReleaseSourceManifestEntry(
                    path,
                    HasExecutableBit(entry.Mode) ? "100755" : "100644",
                    entry.Length,
                    sha256));
                if (files.Count > MaximumManifestEntries)
                {
                    throw new InvalidDataException("The tagged source manifest exceeds its entry limit.");
                }
            }

            var ordered = files.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ToArray();
            ValidateManifestEntries(ordered);
            return new TaggedArchive(archiveSha256, ManifestDigest(ordered), ordered);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private static DirectoryManifest ReadDirectoryManifest(
        string sourceRoot,
        IReadOnlyList<string> generatedRoots,
        bool ignoreRootFinderMetadata)
    {
        var root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DirectoryNotFoundException("The sealed source root must be an existing regular directory.");
        }

        var files = new List<ReleaseSourceManifestEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (generatedRoots.Any(generated => IsPathWithin(relativePath, generated)))
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException($"Generated release root {relativePath} is a symbolic-link boundary.");
                    }

                    continue;
                }

                var attributes = File.GetAttributes(path);
                if (ignoreRootFinderMetadata
                    && string.Equals(relativePath, RootFinderMetadataPath, StringComparison.Ordinal)
                    && (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"The release source contains a symbolic-link boundary: {relativePath}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }

                var info = new FileInfo(path);
                files.Add(new ReleaseSourceManifestEntry(
                    RequireRelativePath(relativePath),
                    IsExecutable(path) ? "100755" : "100644",
                    info.Length,
                    CampaignFiles.Sha256File(path)));
                if (files.Count > MaximumManifestEntries)
                {
                    throw new InvalidDataException("The release source manifest exceeds its entry limit.");
                }
            }
        }

        var ordered = files.OrderBy(static item => item.RelativePath, StringComparer.Ordinal).ToArray();
        ValidateManifestEntries(ordered);
        return new DirectoryManifest(ManifestDigest(ordered), ordered);
    }

    private static void ValidateManifestEntries(IReadOnlyList<ReleaseSourceManifestEntry> files)
    {
        string? previous = null;
        foreach (var file in files)
        {
            var path = RequireRelativePath(file.RelativePath);
            if (previous is not null && string.CompareOrdinal(previous, path) >= 0)
            {
                throw new InvalidDataException("The source manifest paths must be sorted and unique.");
            }

            if (file.Mode is not ("100644" or "100755") || file.Bytes < 0)
            {
                throw new InvalidDataException($"The source manifest metadata is invalid for {path}.");
            }

            RequireHex(file.Sha256, 64, $"source file SHA-256 for {path}");
            previous = path;
        }
    }

    private static string ManifestDigest(IReadOnlyList<ReleaseSourceManifestEntry> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            Append(hash, file.Mode);
            Append(hash, file.RelativePath);
            Append(hash, file.Bytes.ToString(CultureInfo.InvariantCulture));
            Append(hash, file.Sha256);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());

        static void Append(IncrementalHash hash, string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData([0]);
        }
    }

    private static bool ManifestsEqual(
        IReadOnlyList<ReleaseSourceManifestEntry> expected,
        IReadOnlyList<ReleaseSourceManifestEntry> actual) =>
        expected.SequenceEqual(actual);

    private static bool ContainsPath(
        IReadOnlyList<ReleaseSourceManifestEntry> files,
        string relativePath) =>
        files.Any(file => string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal));

    private static void RequireSameManifest(
        IReadOnlyList<ReleaseSourceManifestEntry> expected,
        IReadOnlyList<ReleaseSourceManifestEntry> actual,
        string message)
    {
        if (!ManifestsEqual(expected, actual))
        {
            throw new InvalidDataException(message);
        }
    }

    private static void RequireCanonicalRepository(string repository)
    {
        var origin = Git(repository, "config", "--get", "remote.origin.url");
        if (origin is not ("git@github.com:terion-labs/asura.git"
            or "https://github.com/terion-labs/asura.git"
            or "https://github.com/terion-labs/asura"))
        {
            throw new InvalidDataException("The source seal requires the canonical Asura repository.");
        }
    }

    private static void RequireExactHead(string repository, string expectedCommit, string expectedTree)
    {
        if (!string.Equals(Git(repository, "rev-parse", "HEAD"), expectedCommit, StringComparison.Ordinal)
            || !string.Equals(Git(repository, "rev-parse", "HEAD^{tree}"), expectedTree, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The checkout does not match the requested release commit and tree.");
        }
    }

    private static void RequireTagTarget(string repository, string tag, string expectedCommit)
    {
        if (!string.Equals(Git(repository, "rev-list", "-n", "1", tag), expectedCommit, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The release tag does not resolve to the sealed source commit.");
        }
    }

    private static void RequireCleanTrackedState(string repository)
    {
        if (GitExitCode(repository, ["diff", "--quiet", "--exit-code", "--"]) == 1)
        {
            throw new InvalidDataException("The release checkout contains tracked working-tree changes.");
        }

        if (GitExitCode(repository, ["diff", "--cached", "--quiet", "--exit-code", "--"]) == 1)
        {
            throw new InvalidDataException("The release checkout contains staged index changes.");
        }
    }

    private static string Git(string repository, params string[] arguments)
    {
        var start = GitStartInfo(repository, arguments, redirectOutput: true);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"git {arguments[0]} failed: {error.Trim()}");
        }

        return output.TrimEnd('\r', '\n');
    }

    private static int GitExitCode(string repository, IReadOnlyList<string> arguments)
    {
        var start = GitStartInfo(repository, arguments, redirectOutput: false);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode > 1)
        {
            throw new InvalidDataException($"git {arguments[0]} failed: {error.Trim()}");
        }

        return process.ExitCode;
    }

    private static void WriteGitArchive(string repository, string commit, string destination)
    {
        var start = GitStartInfo(repository, ["archive", "--format=tar", commit], redirectOutput: true);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git archive.");
        using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            process.StandardOutput.BaseStream.CopyTo(file);
        }

        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"git archive failed: {error.Trim()}");
        }
    }

    private static ProcessStartInfo GitStartInfo(
        string repository,
        IReadOnlyList<string> arguments,
        bool redirectOutput)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = redirectOutput,
            UseShellExecute = false,
            WorkingDirectory = repository,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    private static bool HasExecutableBit(UnixFileMode mode) =>
        (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Release source seals require Unix file-mode semantics.");
        }

        return HasExecutableBit(File.GetUnixFileMode(path));
    }

    private static string RequireRelativePath(string value)
    {
        if (value.Length is < 1 or > 1024
            || value.Contains('\\', StringComparison.Ordinal)
            || value.Contains('\0', StringComparison.Ordinal)
            || Path.IsPathRooted(value)
            || value.Split('/').Any(static part => part is "" or "." or ".."))
        {
            throw new InvalidDataException("The source manifest contains a non-canonical path.");
        }

        return value;
    }

    private static bool IsGeneratedPath(string relativePath) =>
        GeneratedRoots.Any(generated => IsPathWithin(relativePath, generated));

    private static bool IsPathWithin(string relativePath, string root) =>
        string.Equals(relativePath, root, StringComparison.Ordinal)
        || relativePath.StartsWith(root + "/", StringComparison.Ordinal);

    private static void RequireExternalPath(string candidate, params string[] protectedRoots)
    {
        foreach (var root in protectedRoots.Select(Path.GetFullPath))
        {
            if (string.Equals(candidate, root, StringComparison.Ordinal)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Release seal and identity outputs must remain outside source roots.");
            }
        }
    }

    private static void RequireTag(string tag)
    {
        var versionParts = tag.Length > 1 ? tag[1..].Split('.') : [];
        if (tag.Length > 32
            || !tag.StartsWith('v')
            || versionParts.Length != 3
            || versionParts.Any(static part => part.Length is < 1 or > 9
                || !part.All(char.IsAsciiDigit)))
        {
            throw new InvalidDataException("A source seal requires a three-part v-prefixed version tag.");
        }
    }

    private static void RequireHex(string value, int length, string field)
    {
        if (value.Length != length
            || !value.All(static character => char.IsAsciiHexDigit(character) && !char.IsUpper(character)))
        {
            throw new InvalidDataException($"The {field} must be {length} lowercase hexadecimal characters.");
        }
    }

    private sealed record TaggedArchive(
        string ArchiveSha256,
        string ManifestSha256,
        IReadOnlyList<ReleaseSourceManifestEntry> Files);

    private sealed record DirectoryManifest(
        string ManifestSha256,
        IReadOnlyList<ReleaseSourceManifestEntry> Files);
}

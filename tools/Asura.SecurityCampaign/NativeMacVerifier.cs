using System.Diagnostics;
using System.IO.Compression;
using Asura.Packaging;

namespace Asura.SecurityCampaign;

internal static class NativeMacVerifier
{
    public static void Verify(
        string packagePath,
        string expectedTeamIdentifier,
        string expectedCertificateSha256)
    {
        var package = Path.GetFullPath(packagePath);
        Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "--verbose=2", package]);
        var details = Run("/usr/bin/codesign", ["--display", "--verbose=4", package]);
        if (!details.Split('\n').Any(line => string.Equals(
                line.Trim(),
                "TeamIdentifier=" + expectedTeamIdentifier,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The package TeamIdentifier differs from signing evidence.");
        }

        var certificateDirectory = Path.Combine(
            Path.GetTempPath(),
            $"asura-certificate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(certificateDirectory);
        try
        {
            var prefix = Path.Combine(certificateDirectory, "certificate");
            Run("/usr/bin/codesign", ["--display", $"--extract-certificates={prefix}", package]);
            var certificate = prefix + "0";
            if (!string.Equals(
                    CampaignFiles.Sha256File(certificate, 1024 * 1024),
                    expectedCertificateSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("The package signing certificate differs from signing evidence.");
            }
        }
        finally
        {
            Directory.Delete(certificateDirectory, recursive: true);
        }

        Run("/usr/bin/xcrun", ["stapler", "validate", package]);
        Run("/usr/sbin/spctl", ["--assess", "--type", "execute", "--verbose=2", package]);

        var contents = Path.Combine(package, "Contents");
        var executable = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");
        var licenses = Path.Combine(resources, "Licenses");
        var nativeLicenses = Path.Combine(licenses, "Native");
        NativeTerminalPackageProvenance.ValidateAfterCodeSigning(
            executable,
            resources,
            Path.Combine(resources, "Native"),
            licenses,
            Path.Combine(nativeLicenses, "native-terminal-components.json"),
            Path.Combine(nativeLicenses, "native-terminal-build-receipt.json"));
    }

    private static string Run(string fileName, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(fileName)} rejected the candidate: {error.Trim()}");
        }

        return output + "\n" + error;
    }
}

internal sealed class ExtractedMacCandidate : IDisposable
{
    private const int MaximumArchiveEntries = 40_000;
    private const int MaximumPathDepth = 64;
    private const long MaximumExpandedBytes = 8L * 1024 * 1024 * 1024;
    private bool _disposed;

    private ExtractedMacCandidate(string directory)
    {
        Directory = directory;
        PackagePath = Path.Combine(directory, "Asura.app");
    }

    public string Directory { get; }

    public string PackagePath { get; }

    public static ExtractedMacCandidate Open(string archivePath)
    {
        var archive = Path.GetFullPath(archivePath);
        InspectArchive(archive);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"asura-candidate-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            RunDitto(archive, directory);
            if (!System.IO.Directory.Exists(Path.Combine(directory, "Asura.app")))
            {
                throw new InvalidDataException("The archive does not contain top-level Asura.app.");
            }

            return new ExtractedMacCandidate(directory);
        }
        catch
        {
            System.IO.Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public void Dispose()
    {
        if (!_disposed && System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }

        _disposed = true;
    }

    private static void InspectArchive(string archivePath)
    {
        _ = CampaignFiles.Sha256File(archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidDataException("The release archive has an invalid entry count.");
        }

        long bytes = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if ((path.Length > 0 && path[0] == '/')
                || parts.Length == 0
                || parts.Length > MaximumPathDepth
                || parts.Any(static part => part is "." or "..")
                || parts[0] is not ("Asura.app" or "__MACOSX"))
            {
                throw new InvalidDataException($"The release archive contains unsafe entry {entry.FullName}.");
            }

            bytes = checked(bytes + entry.Length);
            if (bytes > MaximumExpandedBytes)
            {
                throw new InvalidDataException("The release archive exceeds the expanded byte budget.");
            }
        }
    }

    private static void RunDitto(string archive, string destination)
    {
        var start = new ProcessStartInfo("/usr/bin/ditto")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-x", "-k", archive, destination })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start ditto.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidDataException("ditto could not extract the release archive: " + error.Trim());
        }
    }
}

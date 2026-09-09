using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Asura.Packaging;

namespace Asura.AccessibilityAcceptance.Tests;

public sealed class VelopackMacOsReleaseTests : IDisposable
{
    private const string Version = "1.2.3";
    private const string Channel = "osx-arm64-stable";
    private const string PackageName =
        "sh.asura-1.2.3-osx-arm64-stable-full.nupkg";

    private readonly string _temporaryDirectory =
        Directory.CreateTempSubdirectory(
            "asura-velopack-release-tests-").FullName;

    [Fact]
    public void Validator_accepts_the_exact_feed_package_and_portable_app()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var command = CreateRelease();

        var inspection = VelopackMacOsRelease.Validate(command);

        Assert.Equal(PackageName, inspection.PackageFileName);
        Assert.Equal(4, inspection.ApplicationFileCount);
    }

    [Fact]
    public void Validator_rejects_a_feed_digest_for_another_package()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var command = CreateRelease();
        WriteFeed(command.FullPackagePath, new string('0', 64));

        Assert.Throws<InvalidDataException>(() =>
            VelopackMacOsRelease.Validate(command));
    }

    [Fact]
    public void Validator_rejects_package_content_that_differs_from_the_app()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var command = CreateRelease(packagePayload: "substituted");

        Assert.Throws<InvalidDataException>(() =>
            VelopackMacOsRelease.Validate(command));
    }

    [Fact]
    public void Validator_rejects_any_second_application_link()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var command = CreateRelease();
        File.CreateSymbolicLink(
            Path.Combine(command.ApplicationPath, "linked-payload"),
            "Contents/payload");

        Assert.Throws<InvalidDataException>(() =>
            VelopackMacOsRelease.Validate(command));
    }

    public void Dispose() => Directory.Delete(
        _temporaryDirectory,
        recursive: true);

    private VelopackMacOsReleaseCommand CreateRelease(
        string packagePayload = "payload")
    {
        var application = Path.Combine(_temporaryDirectory, "Asura.app");
        var macOs = Path.Combine(application, "Contents", "MacOS");
        var resources = Path.Combine(application, "Contents", "Resources");
        Directory.CreateDirectory(macOs);
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(application, "Contents", "payload"), "payload");
        File.WriteAllText(Path.Combine(macOs, "UpdateMac"), "updater");
        File.WriteAllText(Path.Combine(resources, "sq.version"), Metadata());
        File.CreateSymbolicLink(
            Path.Combine(macOs, "sq.version"),
            "../Resources/sq.version");

        var package = Path.Combine(_temporaryDirectory, PackageName);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "sh.asura.nuspec", Metadata());
            WriteEntry(
                archive,
                "lib/app/Contents/payload",
                packagePayload);
            WriteEntry(
                archive,
                "lib/app/Contents/MacOS/UpdateMac",
                "updater");
            WriteEntry(
                archive,
                "lib/app/Contents/Resources/sq.version",
                Metadata());
            WriteEntry(
                archive,
                "lib/app/Contents/MacOS/sq.version.__symlink",
                "../Resources/sq.version");
        }

        WriteFeed(package);
        return new VelopackMacOsReleaseCommand(
            _temporaryDirectory,
            package,
            application,
            Version,
            Channel);
    }

    private void WriteFeed(
        string package,
        string? packageSha256 = null)
    {
        var packageFile = new FileInfo(package);
        packageSha256 ??= Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(package)));
        File.WriteAllText(
            Path.Combine(_temporaryDirectory, $"releases.{Channel}.json"),
            JsonSerializer.Serialize(new
            {
                Assets = new[]
                {
                    new
                    {
                        PackageId = "sh.asura",
                        Version,
                        Type = "Full",
                        FileName = PackageName,
                        SHA1 = new string('0', 40),
                        SHA256 = packageSha256,
                        Size = packageFile.Length,
                    },
                },
            }));
    }

    private static string Metadata() =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
          <metadata>
            <id>sh.asura</id>
            <version>{Version}</version>
            <channel>{Channel}</channel>
            <mainExe>Contents/MacOS/Asura</mainExe>
            <os>osx</os>
            <rid>osx-arm64</rid>
            <machineArchitecture>arm64</machineArchitecture>
          </metadata>
        </package>
        """;

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}

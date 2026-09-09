using Asura.Application.ApplicationUpdates;

namespace Asura.Updates.Tests;

public sealed class DistributionIdentityReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"asura-update-tests-{Guid.NewGuid():N}");

    public DistributionIdentityReaderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Direct_manifest_selects_the_matching_Velopack_channel()
    {
        var path = WriteManifest(
            "github-release",
            "velopack",
            "osx-arm64-stable",
            "osx-arm64");

        var parsed = DistributionIdentityReader.TryRead(
            path,
            "osx-arm64",
            out var identity);

        Assert.True(parsed);
        Assert.Equal(DistributionSource.GitHubRelease, identity.Source);
        Assert.Equal(ApplicationUpdateStrategy.Velopack, identity.UpdateStrategy);
        Assert.Equal("osx-arm64-stable", identity.Channel);
    }

    [Fact]
    public void Store_manifest_never_selects_Velopack()
    {
        var path = WriteManifest(
            "apple-app-store",
            "platform-managed",
            "osx-arm64-stable",
            "osx-arm64");

        var parsed = DistributionIdentityReader.TryRead(
            path,
            "osx-arm64",
            out var identity);

        Assert.True(parsed);
        Assert.Equal(DistributionSource.AppleAppStore, identity.Source);
        Assert.Equal(
            ApplicationUpdateStrategy.PlatformManaged,
            identity.UpdateStrategy);
    }

    [Fact]
    public void Manifest_for_another_runtime_is_rejected()
    {
        var path = WriteManifest(
            "github-release",
            "velopack",
            "win-x64-stable",
            "win-x64");

        Assert.False(DistributionIdentityReader.TryRead(
            path,
            "osx-arm64",
            out _));
    }

    [Fact]
    public void Unknown_fields_are_rejected()
    {
        var path = Path.Combine(_directory, "distribution.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 1,
              "source": "github-release",
              "updateStrategy": "velopack",
              "packageId": "sh.asura",
              "channel": "osx-arm64-stable",
              "runtimeIdentifier": "osx-arm64",
              "feedUrl": "https://attacker.invalid"
            }
            """);

        Assert.False(DistributionIdentityReader.TryRead(
            path,
            "osx-arm64",
            out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteManifest(
        string source,
        string strategy,
        string channel,
        string runtimeIdentifier)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "source": "{{source}}",
              "updateStrategy": "{{strategy}}",
              "packageId": "sh.asura",
              "channel": "{{channel}}",
              "runtimeIdentifier": "{{runtimeIdentifier}}"
            }
            """);
        return path;
    }
}

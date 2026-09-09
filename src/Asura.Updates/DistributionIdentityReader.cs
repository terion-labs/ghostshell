using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.Application;
using Asura.Application.ApplicationUpdates;

namespace Asura.Updates;

internal static class DistributionIdentityReader
{
    private const string ManifestName = "distribution.json";

    public static DistributionIdentity ReadInstalled()
    {
        foreach (var path in CandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            return TryRead(path, RuntimeInformation.RuntimeIdentifier, out var identity)
                ? identity
                : DistributionIdentity.Development;
        }

        return DistributionIdentity.Development;
    }

    internal static bool TryRead(
        string path,
        string runtimeIdentifier,
        out DistributionIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        identity = DistributionIdentity.Development;

        try
        {
            using var stream = File.OpenRead(path);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            var context = new UpdateJsonContext(options);
            var manifest = JsonSerializer.Deserialize(
                stream,
                context.DistributionManifest);
            if (manifest is null || !IsValid(manifest, runtimeIdentifier))
            {
                return false;
            }

            identity = new(
                ParseSource(manifest.Source),
                ParseStrategy(manifest.UpdateStrategy),
                manifest.Channel);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsValid(
        DistributionManifest manifest,
        string runtimeIdentifier)
    {
        if (manifest.SchemaVersion != 1
            || !string.Equals(
                manifest.PackageId,
                ProductIdentity.BundleIdentifier,
                StringComparison.Ordinal)
            || !string.Equals(
                manifest.RuntimeIdentifier,
                runtimeIdentifier,
                StringComparison.Ordinal)
            || !IsChannelValid(manifest.Channel, runtimeIdentifier))
        {
            return false;
        }

        return (manifest.Source, manifest.UpdateStrategy) switch
        {
            ("github-release", "velopack") => true,
            ("apple-app-store", "platform-managed") => true,
            ("microsoft-store", "platform-managed") => true,
            ("linux-package-manager", "platform-managed") => true,
            _ => false,
        };
    }

    private static bool IsChannelValid(string channel, string runtimeIdentifier)
    {
        var prefix = runtimeIdentifier + "-";
        if (!channel.StartsWith(prefix, StringComparison.Ordinal)
            || channel.Length <= prefix.Length)
        {
            return false;
        }

        return channel.All(character =>
            character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '-');
    }

    private static DistributionSource ParseSource(string source) => source switch
    {
        "github-release" => DistributionSource.GitHubRelease,
        "apple-app-store" => DistributionSource.AppleAppStore,
        "microsoft-store" => DistributionSource.MicrosoftStore,
        "linux-package-manager" => DistributionSource.LinuxPackageManager,
        _ => throw new InvalidOperationException("Validated distribution source was lost."),
    };

    private static ApplicationUpdateStrategy ParseStrategy(string strategy) =>
        strategy switch
        {
            "velopack" => ApplicationUpdateStrategy.Velopack,
            "platform-managed" => ApplicationUpdateStrategy.PlatformManaged,
            _ => throw new InvalidOperationException("Validated update strategy was lost."),
        };

    private static IEnumerable<string> CandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, ManifestName);
        yield return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "Resources",
            ManifestName));
    }
}

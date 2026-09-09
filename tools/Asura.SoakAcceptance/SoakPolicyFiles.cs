using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asura.AccessibilityAcceptance;

namespace Asura.SoakAcceptance;

internal static class SoakJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

internal sealed record LoadedPolicy(SoakPolicy Policy, string Sha256);

internal static class SoakPolicyFiles
{
    private const int MaximumPolicyBytes = 128_000;

    public static LoadedPolicy Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists || info.Length is <= 0 or > MaximumPolicyBytes)
        {
            throw new InvalidDataException("The soak policy is missing, empty, or exceeds 128 KB.");
        }

        var bytes = File.ReadAllBytes(info.FullName);
        var policy = JsonSerializer.Deserialize<SoakPolicy>(bytes, SoakJson.Options)
            ?? throw new InvalidDataException("The soak policy JSON was empty.");
        Validate(policy);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new LoadedPolicy(policy, digest);
    }

    public static void Validate(SoakPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.SchemaVersion != 1
            || !string.Equals(policy.PolicyKind, "asura-macos-arm64-soak-policy", StringComparison.Ordinal)
            || !EvidenceSanitizer.IsValidIdentifier(policy.PolicyVersion)
            || !EvidenceSanitizer.IsValidIdentifier(policy.ReferenceConfigurationId)
            || policy.RatifiedAtUtc.Offset != TimeSpan.Zero
            || policy.RatifiedAtUtc == default)
        {
            throw new InvalidDataException("The policy header is not a ratified v1 macOS-arm64 policy.");
        }

        if (policy.Scenarios.Count != SoakCatalog.Scenarios.Count)
        {
            throw new InvalidDataException("The policy must budget every catalog scenario exactly once.");
        }

        for (var index = 0; index < SoakCatalog.Scenarios.Count; index++)
        {
            var expected = SoakCatalog.Scenarios[index];
            var budget = policy.Scenarios[index];
            if (!string.Equals(budget.Id, expected.Id, StringComparison.Ordinal)
                || !string.Equals(budget.LoadUnit, expected.LoadUnit, StringComparison.Ordinal)
                || budget.DurationSeconds < 60
                || budget.RequiredLoad <= 0
                || budget.MaximumWorkingSetGrowthBytes <= 0
                || budget.MaximumFailures < 0
                || budget.CleanupTimeoutSeconds is < 5 or > 300
                || budget.MaximumLiveProcessesAfterCleanup != 0)
            {
                throw new InvalidDataException($"Scenario budget {expected.Id} is missing, reordered, unbounded, or invalid.");
            }
        }
    }
}

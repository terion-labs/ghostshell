using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Asura.SoakAcceptance;

internal sealed record SoakEvidencePaths(string Directory, string Json, string Markdown, string Digest);

internal static class SoakEvidenceFiles
{
    public static SoakEvidencePaths Write(string root, SoakReceipt receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Validate(receipt);
        var directory = Path.Combine(Path.GetFullPath(root), $"soak-{receipt.StartedAtUtc:yyyyMMdd-HHmmss}-{receipt.Build.PackageManifestSha256[..12]}");
        Directory.CreateDirectory(directory);
        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new IOException("Refusing to overwrite an existing soak receipt directory.");
        }

        var jsonPath = Path.Combine(directory, "receipt.json");
        var markdownPath = Path.Combine(directory, "receipt.md");
        var digestPath = Path.Combine(directory, "receipt.json.sha256");
        var json = JsonSerializer.SerializeToUtf8Bytes(receipt, SoakJson.Options);
        var bytes = new byte[json.Length + 1];
        json.CopyTo(bytes, 0);
        bytes[^1] = (byte)'\n';
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        File.WriteAllText(markdownPath, RenderMarkdown(receipt, digest), new UTF8Encoding(false));
        File.WriteAllText(digestPath, $"{digest}  receipt.json\n", new UTF8Encoding(false));
        File.WriteAllBytes(jsonPath, bytes);
        return new SoakEvidencePaths(directory, jsonPath, markdownPath, digestPath);
    }

    public static void Validate(SoakReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        SoakPolicyFiles.Validate(receipt.Policy);
        if (receipt.SchemaVersion != 1
            || !string.Equals(receipt.EvidenceKind, "asura-macos-arm64-release-soak", StringComparison.Ordinal)
            || !string.Equals(receipt.RunnerVersion, "1.0.0", StringComparison.Ordinal)
            || !string.Equals(receipt.CatalogSha256, SoakCatalog.Sha256, StringComparison.Ordinal)
            || receipt.PolicySha256.Length != 64
            || !string.Equals(receipt.Host.OsArchitecture, "Arm64", StringComparison.Ordinal)
            || !string.Equals(receipt.Host.ProcessArchitecture, "Arm64", StringComparison.Ordinal)
            || receipt.Scenarios.Count != SoakCatalog.Scenarios.Count)
        {
            throw new InvalidDataException("The soak receipt header or scenario set is invalid.");
        }

        for (var index = 0; index < receipt.Scenarios.Count; index++)
        {
            var actual = receipt.Scenarios[index];
            if (!string.Equals(actual.Id, SoakCatalog.Scenarios[index].Id, StringComparison.Ordinal)
                || actual.FailureCodes.Any(code => !IsFailureCode(code))
                || actual.Resources.SampleCount < 0
                || actual.Resources.CapturedProcessCount < 0)
            {
                throw new InvalidDataException($"Scenario observation {actual.Id} is invalid.");
            }
        }

        var calculated = ResolveOverall(receipt.Scenarios, receipt.PackageUnchanged);
        if (calculated != receipt.OverallResult)
        {
            throw new InvalidDataException("The overall result does not match the fail-closed scenario results.");
        }
    }

    public static SoakStatus ResolveOverall(IReadOnlyList<ScenarioObservation> scenarios, bool packageUnchanged)
    {
        if (!packageUnchanged || scenarios.Any(s => s.MachineResult == SoakStatus.Fail))
        {
            return SoakStatus.Fail;
        }

        return scenarios.All(s => s.MachineResult == SoakStatus.Pass)
            ? SoakStatus.Pass
            : SoakStatus.Blocked;
    }

    private static bool IsFailureCode(string value) =>
        value.Length is >= 3 and <= 64
        && value.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-');

    private static string RenderMarkdown(SoakReceipt receipt, string digest)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Asura macOS arm64 release soak receipt");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Result: `{receipt.OverallResult}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Policy: `{receipt.Policy.PolicyVersion}` (`{receipt.PolicySha256}`)");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Package manifest: `{receipt.Build.PackageManifestSha256}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Host: `{receipt.Host.ReferenceConfigurationId}` / `{receipt.Host.HostFingerprint}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Receipt SHA-256: `{digest}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Package unchanged: `{receipt.PackageUnchanged}`");
        builder.AppendLine();
        builder.AppendLine("| Scenario | Result | Load | Failures | RSS growth | Cleanup |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var scenario in receipt.Scenarios)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| `{scenario.Id}` | `{scenario.MachineResult}` | {scenario.CompletedLoad} | {scenario.ObservedFailures} | {scenario.Resources.WorkingSetGrowthBytes} | {scenario.CleanupPassed} |");
        }

        builder.AppendLine();
        builder.AppendLine("This receipt contains only bounded counters, stable reason codes, package/build fingerprints, and a one-way truncated host fingerprint. It excludes commands, arguments, environment values, URLs, file paths, terminal/provider/MCP content, credentials, and operator notes.");
        return builder.ToString();
    }
}

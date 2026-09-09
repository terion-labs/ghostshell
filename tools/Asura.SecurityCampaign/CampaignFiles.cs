using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asura.SecurityCampaign;

internal static class CampaignFiles
{
    private const int MaximumJsonBytes = 16 * 1024 * 1024;

    public static readonly JsonSerializerOptions StrictJson = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 24,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static T ReadJson<T>(string path)
    {
        var bytes = ReadFile(path, MaximumJsonBytes);
        try
        {
            RejectDuplicateProperties(bytes, path);
            return JsonSerializer.Deserialize<T>(bytes, StrictJson)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{path} is malformed or has unknown fields.", exception);
        }
    }

    public static byte[] ReadFile(string path, long maximumBytes)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("Evidence must be an existing regular file.", fullPath);
        }

        if (info.Length > maximumBytes)
        {
            throw new InvalidDataException($"Evidence exceeds {maximumBytes} bytes: {fullPath}");
        }

        return File.ReadAllBytes(fullPath);
    }

    public static FileEvidence InspectFile(string kind, string path, string relativePath, long maximumBytes)
    {
        var bytes = ReadFile(path, maximumBytes);
        return new FileEvidence(kind, relativePath, bytes.LongLength, Sha256(bytes));
    }

    public static string Sha256(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Sha256File(string path, long maximumBytes = 10L * 1024 * 1024 * 1024)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("Evidence must be an existing regular file.", fullPath);
        }

        if (info.Length > maximumBytes)
        {
            throw new InvalidDataException($"Evidence exceeds {maximumBytes} bytes: {fullPath}");
        }

        using var stream = File.OpenRead(fullPath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static void WriteReceipt(string outputDirectory, CampaignReceipt receipt)
    {
        var destination = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException("The evidence output path must not already exist.");
        }

        Directory.CreateDirectory(destination);
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(receipt, StrictJson);
            var markdown = RenderMarkdown(receipt);
            File.WriteAllText(
                Path.Combine(destination, "receipt.md"),
                markdown,
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(destination, "receipt.json.sha256"),
                Sha256(json) + "  receipt.json\n",
                new UTF8Encoding(false));
            File.WriteAllBytes(Path.Combine(destination, "receipt.json"), json);
        }
        catch
        {
            Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    public static CampaignReceipt ReadReceipt(string evidenceDirectory)
    {
        var root = Path.GetFullPath(evidenceDirectory);
        var expected = new HashSet<string>(["receipt.json", "receipt.json.sha256", "receipt.md"], StringComparer.Ordinal);
        var actual = Directory.EnumerateFileSystemEntries(root)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidDataException("The evidence directory must contain exactly the three receipt files.");
        }

        var receiptPath = Path.Combine(root, "receipt.json");
        var bytes = ReadFile(receiptPath, MaximumJsonBytes);
        var expectedSidecar = Sha256(bytes) + "  receipt.json\n";
        if (!string.Equals(
                File.ReadAllText(Path.Combine(root, "receipt.json.sha256"), Encoding.UTF8),
                expectedSidecar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The receipt checksum sidecar does not match receipt.json.");
        }

        var receipt = ReadJson<CampaignReceipt>(receiptPath);
        if (!string.Equals(
                File.ReadAllText(Path.Combine(root, "receipt.md"), Encoding.UTF8),
                RenderMarkdown(receipt),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("receipt.md is not the canonical rendering of receipt.json.");
        }

        return receipt;
    }

    private static string RenderMarkdown(CampaignReceipt receipt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Asura security campaign receipt");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Evidence class: `{receipt.EvidenceClass}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Overall: `{receipt.Overall}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Source commit: `{receipt.Source.Commit}`");
        if (receipt.Source.SourceSealSha256 is not null)
        {
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"Source seal: `{receipt.Source.SourceSealSha256}`");
            builder.AppendLine(
                CultureInfo.InvariantCulture,
                $"Source manifest: `{receipt.Source.SourceManifestSha256}`");
        }
        builder.AppendLine();
        builder.AppendLine("## Cases");
        builder.AppendLine();
        foreach (var item in receipt.Cases.OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- `{item.Id}`: `{item.Result}`");
        }

        return builder.ToString();
    }

    private static void RejectDuplicateProperties(byte[] bytes, string path)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 24,
        });
        InspectElement(document.RootElement, path);

        static void InspectElement(JsonElement element, string documentPath)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException($"{documentPath} contains duplicate property {property.Name}.");
                    }

                    InspectElement(property.Value, documentPath);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    InspectElement(item, documentPath);
                }
            }
        }
    }
}

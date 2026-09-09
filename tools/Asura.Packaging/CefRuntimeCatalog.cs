using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asura.Packaging;

internal sealed record CefRuntimeDistribution(
    string Rid,
    string Platform,
    string ArchiveSha1,
    string ArchiveSha256,
    string CefLicenseSha256,
    string CefCreditsSha256);

internal sealed record CefRuntimeCatalog(
    byte[] Content,
    string DocumentCreatedUtc,
    string CefVersion,
    string BindingRepository,
    string BindingCommit,
    string BindingVersion,
    string BindingPatchSetSha256,
    string BindingSourceSnapshotSha256,
    string BindingLicenseSha256,
    IReadOnlyList<string> ReleaseBlockers,
    IReadOnlyDictionary<string, CefRuntimeDistribution> Distributions)
{
    private const int MaximumCatalogBytes = 128 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 12,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static CefRuntimeCatalog Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] content;
        using (var stream = RegularPackageFileReader.Open(path, out var file))
        {
            if (file.Length is <= 0 or > MaximumCatalogBytes)
            {
                throw new InvalidDataException(
                    "The CEF runtime catalog is outside the allowed size range.");
            }

            content = new byte[(int)file.Length];
            stream.ReadExactly(content);
        }

        CatalogDocument document;
        try
        {
            using var parsed = JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 12,
                });
            ValidateNoDuplicateProperties(parsed.RootElement);
            document = JsonSerializer.Deserialize<CatalogDocument>(content, JsonOptions)
                ?? throw new InvalidDataException("The CEF runtime catalog is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The CEF runtime catalog is malformed.",
                exception);
        }

        if (document.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                "The CEF runtime catalog schemaVersion must be 1.");
        }

        ValidateText(document.CefVersion, "cefVersion", 120);
        if (!DateTimeOffset.TryParseExact(
                document.DocumentCreatedUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out _))
        {
            throw new InvalidDataException(
                "The CEF runtime catalog documentCreatedUtc is invalid.");
        }

        ValidateHttpsUri(document.BindingRepository, "bindingRepository");
        ValidateHex(document.BindingCommit, 40, "bindingCommit");
        ValidateText(document.BindingVersion, "bindingVersion", 64);
        ValidateHex(
            document.BindingPatchSetSha256,
            64,
            "bindingPatchSetSha256");
        ValidateHex(
            document.BindingSourceSnapshotSha256,
            64,
            "bindingSourceSnapshotSha256");

        ValidateHex(document.BindingLicenseSha256, 64, "bindingLicenseSha256");
        if (document.ReleaseBlockers is null || document.ReleaseBlockers.Count == 0)
        {
            throw new InvalidDataException(
                "The CEF runtime catalog must retain releaseBlockers.");
        }

        foreach (var blocker in document.ReleaseBlockers)
        {
            ValidateText(blocker, "release blocker", 1_000);
        }

        if (document.Distributions is null || document.Distributions.Count != 5)
        {
            throw new InvalidDataException(
                "The CEF runtime catalog must contain the five supported distributions.");
        }

        var distributions = new Dictionary<string, CefRuntimeDistribution>(
            StringComparer.Ordinal);
        foreach (var distribution in document.Distributions)
        {
            if (distribution is null)
            {
                throw new InvalidDataException(
                    "The CEF runtime catalog contains a null distribution.");
            }

            ValidateText(distribution.Rid, "distribution rid", 32);
            ValidateText(distribution.Platform, "distribution platform", 32);
            ValidateHex(distribution.ArchiveSha1, 40, "distribution archiveSha1");
            ValidateHex(distribution.ArchiveSha256, 64, "distribution archiveSha256");
            ValidateHex(
                distribution.CefLicenseSha256,
                64,
                "distribution cefLicenseSha256");
            ValidateHex(
                distribution.CefCreditsSha256,
                64,
                "distribution cefCreditsSha256");
            if (!distributions.TryAdd(distribution.Rid, distribution))
            {
                throw new InvalidDataException(
                    $"The CEF runtime catalog repeats RID {distribution.Rid}.");
            }
        }

        string[] expectedRids =
        [
            "linux-arm64",
            "linux-x64",
            "osx-arm64",
            "osx-x64",
            "win-x64",
        ];
        if (!distributions.Keys.Order(StringComparer.Ordinal).SequenceEqual(expectedRids, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The CEF runtime catalog has an unexpected RID set.");
        }

        var expectedPlatforms = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["linux-arm64"] = "linuxarm64",
            ["linux-x64"] = "linux64",
            ["osx-arm64"] = "macosarm64",
            ["osx-x64"] = "macosx64",
            ["win-x64"] = "windows64",
        };
        foreach (var (rid, expectedPlatform) in expectedPlatforms)
        {
            if (!string.Equals(distributions[rid].Platform, expectedPlatform, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The CEF runtime catalog has an unexpected platform for {rid}.");
            }
        }

        return new CefRuntimeCatalog(
            content,
            document.DocumentCreatedUtc,
            document.CefVersion,
            document.BindingRepository,
            document.BindingCommit,
            document.BindingVersion,
            document.BindingPatchSetSha256,
            document.BindingSourceSnapshotSha256,
            document.BindingLicenseSha256,
            document.ReleaseBlockers,
            distributions);
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException(
                        $"Duplicate JSON property {property.Name}.");
                }

                ValidateNoDuplicateProperties(property.Value);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            ValidateNoDuplicateProperties(item);
        }
    }

    internal static void ValidateHex(
        [NotNull] string? value,
        int length,
        string description)
    {
        if (value is null
            || value.Length != length
            || value.Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException(
                $"The CEF runtime catalog {description} is invalid.");
        }
    }

    private static void ValidateHttpsUri(
        [NotNull] string? value,
        string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps
, StringComparison.Ordinal) || uri.UserInfo.Length != 0)
        {
            throw new InvalidDataException(
                $"The CEF runtime catalog {description} is invalid.");
        }
    }

    private static void ValidateText(
        [NotNull] string? value,
        string description,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"The CEF runtime catalog {description} is invalid.");
        }
    }

    private sealed class CatalogDocument
    {
        public required int SchemaVersion { get; init; }

        public required string DocumentCreatedUtc { get; init; }

        public required string CefVersion { get; init; }

        public required string BindingRepository { get; init; }

        public required string BindingCommit { get; init; }

        public required string BindingVersion { get; init; }

        public required string BindingPatchSetSha256 { get; init; }

        public required string BindingSourceSnapshotSha256 { get; init; }

        public required string BindingLicenseSha256 { get; init; }

        public required List<string> ReleaseBlockers { get; init; }

        public required List<CefRuntimeDistribution?>? Distributions { get; init; }
    }
}

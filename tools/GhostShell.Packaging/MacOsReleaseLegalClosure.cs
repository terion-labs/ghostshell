using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace GhostShell.Packaging;

internal sealed record MacOsReleaseLegalInspection(
    byte[] Record,
    bool LegalClearance,
    IReadOnlyList<string> ReleaseBlockers,
    IReadOnlyDictionary<string, byte[]> Evidence);

/// <summary>
/// Binds the macOS legal decision to the exact engineering evidence reviewed.
/// A blocked record is valid package evidence, but only a record accepted by
/// the project owner can cross the separate tag-publication boundary.
/// </summary>
internal static class MacOsReleaseLegalClosure
{
    private const int MaximumRecordBytes = 1024 * 1024;
    private const string Format = "ghostshell-macos-release-legal-closure-v1";
    private const string AcceptedReviewStatus = "accepted-by-project-owner";
    private const string AcceptedReviewBasis =
        "documented-engineering-evidence-without-independent-legal-review";
    private const string PendingReviewStatus = "pending-project-owner-decision";
    private const string AcceptedDispositionStatus =
        "accepted-by-owner-for-macos";

    private static readonly string[] RequiredEvidencePaths =
    [
        "LICENSE",
        "assets/macos/product-identity.json",
        "licenses/GPL-3.0.txt",
        "licenses/SMBLIBRARY-LGPL-3.0.txt",
        "licenses/SMBLIBRARY-SOURCE-AND-RELINKING.md",
        "licenses/SMBLIBRARY-SOURCE.json",
        "licenses/SQLCLIENT-MIT.txt",
        "licenses/THIRD-PARTY-NOTICES.md",
        "licenses/cef-runtime-components.json",
        "licenses/managed-components.json",
        "licenses/native-terminal-components.json",
        "licenses/terminal-font-assets.json",
        "native/ghostty-vt/SHELL-INTEGRATION-NOTICE.md",
        "native/sql-language-worker/src/legal/legal-review.tsv",
        "native/sql-language-worker/src/legal/runtime-license-map.tsv",
    ];

    public static MacOsReleaseLegalInspection Validate(
        string recordPath,
        string sourceRoot)
    {
        var root = MacOsPackagePaths.RequireExistingDirectory(
            sourceRoot,
            nameof(sourceRoot));
        var record = ReadRegularFile(recordPath, "macOS release legal record");
        using var document = JsonDocument.Parse(record, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var value = document.RootElement;
        RequireProperties(
            value,
            "schemaVersion",
            "format",
            "platform",
            "legalClearance",
            "releaseBlockers",
            "excludedPlatforms",
            "review",
            "dispositions",
            "evidence");
        RequireInt32(value, "schemaVersion", 1);
        RequireString(value, "format", Format);
        RequireString(value, "platform", "macos-arm64");
        var legalClearance = RequireBoolean(value, "legalClearance");
        var blockers = RequireStringArray(value, "releaseBlockers");
        var excluded = RequireStringArray(value, "excludedPlatforms");
        if (!excluded.SequenceEqual(["windows", "linux"], StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The macOS release legal record must exclude Windows and Linux explicitly.");
        }

        ValidateReview(value.GetProperty("review"), legalClearance, blockers);
        ValidateDispositions(value.GetProperty("dispositions"), legalClearance);
        var evidence = ValidateEvidence(value.GetProperty("evidence"), root);
        return new MacOsReleaseLegalInspection(
            record,
            legalClearance,
            blockers,
            evidence);
    }

    public static void RequirePublicationClearance(
        MacOsReleaseLegalInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        if (!inspection.LegalClearance || inspection.ReleaseBlockers.Count != 0)
        {
            throw new InvalidDataException(
                "macOS release publication is blocked until legalClearance is true "
                + "and releaseBlockers is empty.");
        }
    }

    private static void ValidateReview(
        JsonElement review,
        bool legalClearance,
        IReadOnlyList<string> blockers)
    {
        RequireProperties(
            review,
            "status",
            "basis",
            "reviewedBy",
            "reviewedAtUtc");
        if (legalClearance)
        {
            if (blockers.Count != 0)
            {
                throw new InvalidDataException(
                    "A cleared macOS legal record cannot retain release blockers.");
            }

            RequireString(review, "status", AcceptedReviewStatus);
            RequireString(review, "basis", AcceptedReviewBasis);
            _ = RequireNonEmptyString(review, "reviewedBy");
            var reviewedAt = RequireNonEmptyString(review, "reviewedAtUtc");
            if (!DateTimeOffset.TryParseExact(
                    reviewedAt,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _))
            {
                throw new InvalidDataException(
                    "An owner-accepted macOS legal record requires a valid reviewedAtUtc value.");
            }

            return;
        }

        if (blockers.Count == 0)
        {
            throw new InvalidDataException(
                "A blocked macOS legal record must explain at least one release blocker.");
        }

        RequireString(review, "status", PendingReviewStatus);
        RequireNull(review, "basis");
        RequireNull(review, "reviewedBy");
        RequireNull(review, "reviewedAtUtc");
    }

    private static void ValidateDispositions(
        JsonElement dispositions,
        bool legalClearance)
    {
        var names = new[]
        {
            "managedComponents",
            "nativeTerminalAndShell",
            "cefMacos",
            "sqlLanguageWorker",
        };
        RequireProperties(dispositions, names);
        var acceptedCount = 0;
        foreach (var name in names)
        {
            var disposition = dispositions.GetProperty(name);
            RequireProperties(disposition, "status", "scope", "comment");
            RequireString(disposition, "scope", "macos-arm64");
            _ = RequireNonEmptyString(disposition, "comment");
            var status = RequireNonEmptyString(disposition, "status");
            if (string.Equals(
                    status,
                    AcceptedDispositionStatus,
                    StringComparison.Ordinal))
            {
                acceptedCount++;
            }
            else if (!string.Equals(
                         status,
                         PendingReviewStatus,
                         StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The macOS legal disposition {name} has an invalid status.");
            }
        }

        if (legalClearance && acceptedCount != names.Length)
        {
            throw new InvalidDataException(
                "macOS legal clearance requires an owner-accepted disposition for every nested evidence set.");
        }

        if (!legalClearance && acceptedCount == names.Length)
        {
            throw new InvalidDataException(
                "A blocked macOS legal record must retain a pending nested evidence disposition.");
        }
    }

    private static IReadOnlyDictionary<string, byte[]> ValidateEvidence(
        JsonElement evidence,
        string sourceRoot)
    {
        if (evidence.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "The macOS release legal record evidence must be an array.");
        }

        var expected = new HashSet<string>(RequiredEvidencePaths, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var contentByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var item in evidence.EnumerateArray())
        {
            RequireProperties(item, "path", "sha256");
            var relativePath = RequireNonEmptyString(item, "path");
            var expectedSha256 = RequireNonEmptyString(item, "sha256");
            if (!expected.Remove(relativePath)
                || !seen.Add(relativePath)
                || expectedSha256.Length != 64
                || expectedSha256.Any(character => character is not (>= '0' and <= '9'
                    or >= 'a' and <= 'f')))
            {
                throw new InvalidDataException(
                    "The macOS release legal record has an unexpected evidence entry.");
            }

            var path = ResolveEvidencePath(sourceRoot, relativePath);
            var content = ReadRegularFile(path, $"legal evidence {relativePath}");
            var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The macOS legal evidence hash differs for {relativePath}.");
            }

            contentByPath.Add(relativePath, content);
        }

        if (expected.Count != 0 || seen.Count != RequiredEvidencePaths.Length)
        {
            throw new InvalidDataException(
                "The macOS release legal record does not bind the complete evidence set.");
        }

        return contentByPath;
    }

    private static string ResolveEvidencePath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal)
            || relativePath.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                "The macOS release legal record contains an unsafe evidence path.");
        }

        var path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal)
            ? path
            : throw new InvalidDataException(
                "The macOS release legal record references evidence outside its source root.");
    }

    private static byte[] ReadRegularFile(string path, string description)
    {
        using var stream = RegularPackageFileReader.Open(path, out var inspection);
        if (inspection.Length is < 1 or > MaximumRecordBytes)
        {
            throw new InvalidDataException($"The {description} has an invalid size.");
        }

        var result = new byte[inspection.Length];
        stream.ReadExactly(result);
        return result;
    }

    private static void RequireProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The macOS release legal record has an unexpected shape.");
        }
    }

    private static void RequireInt32(JsonElement value, string name, int expected)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var actual)
            || actual != expected)
        {
            throw new InvalidDataException(
                $"The macOS release legal record {name} value is invalid.");
        }
    }

    private static bool RequireBoolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"The macOS release legal record {name} value is invalid.");
        }

        return property.GetBoolean();
    }

    private static void RequireString(
        JsonElement value,
        string name,
        string expected)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || !string.Equals(property.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The macOS release legal record {name} value is invalid.");
        }
    }

    private static string RequireNonEmptyString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidDataException(
                $"The macOS release legal record {name} value is invalid.");
        }

        return property.GetString()!;
    }

    private static IReadOnlyList<string> RequireStringArray(
        JsonElement value,
        string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"The macOS release legal record {name} value is invalid.");
        }

        var result = property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null)
            .ToArray();
        if (result.Any(string.IsNullOrWhiteSpace)
            || result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw new InvalidDataException(
                $"The macOS release legal record {name} value is invalid.");
        }

        return [.. result.Select(item => item!)];
    }

    private static void RequireNull(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException(
                $"The macOS release legal record {name} value is invalid.");
        }
    }
}

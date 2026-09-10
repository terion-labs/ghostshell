using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Asura.Packaging;

namespace Asura.AccessibilityAcceptance;

public sealed class MacOsReleaseLegalClosureTests : IDisposable
{
    private static readonly string[] EvidencePaths =
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

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-legal-closure-tests-{Guid.NewGuid():N}");

    public MacOsReleaseLegalClosureTests() =>
        Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void Blocked_record_is_valid_evidence_but_cannot_be_published()
    {
        var recordPath = CreateRecord(
            legalClearance: false,
            ["The project owner has not made a release decision."],
            "pending-project-owner-decision",
            reviewedBy: null,
            reviewedAtUtc: null);

        var inspection = MacOsReleaseLegalClosure.Validate(
            recordPath,
            _temporaryDirectory);

        Assert.False(inspection.LegalClearance);
        Assert.Throws<InvalidDataException>(() =>
            MacOsReleaseLegalClosure.RequirePublicationClearance(inspection));
    }

    [Fact]
    public void Owner_approved_checked_in_macos_record_binds_the_current_repository_evidence()
    {
        var repositoryRoot = FindRepositoryRoot();

        var inspection = MacOsReleaseLegalClosure.Validate(
            Path.Combine(repositoryRoot, "licenses", "macos-release-legal.json"),
            repositoryRoot);

        Assert.True(inspection.LegalClearance);
        Assert.Empty(inspection.ReleaseBlockers);
        MacOsReleaseLegalClosure.RequirePublicationClearance(inspection);
    }

    [Theory]
    [InlineData("managed-components.json")]
    [InlineData("workspace-backend-managed-components.json")]
    [InlineData("workspace-backend-x64-managed-components.json")]
    public void Checked_in_component_catalogs_pass_release_schema_validation(string catalogName)
    {
        ManagedComponentEvidenceBuilder.ValidateCatalogFile(
            Path.Combine(FindRepositoryRoot(), "licenses", catalogName));
    }

    [Theory]
    [InlineData("LICENSE.txt")]
    [InlineData("THIRD-PARTY-NOTICES.TXT")]
    public void Catalog_preflight_rejects_trivial_ASP_NET_notice_bounds(string noticeName)
    {
        var source = Path.Combine(FindRepositoryRoot(), "licenses", "managed-components.json");
        var catalog = JsonNode.Parse(File.ReadAllText(source))!;
        var runtime = catalog["dependencies"]!.AsArray().Single(component =>
            component!["identity"]!.GetValue<string>()
                == "runtimepack.Microsoft.AspNetCore.App.Runtime.osx-arm64/10.0.11")!;
        var notice = runtime["notices"]!.AsArray().Single(item =>
            item!["archivePath"]!.GetValue<string>() == noticeName)!;
        notice["minimumBytes"] = 100;
        var path = Path.Combine(_temporaryDirectory, "invalid-catalog.json");
        File.WriteAllText(path, catalog.ToJsonString());

        var error = Assert.Throws<InvalidDataException>(() =>
            ManagedComponentEvidenceBuilder.ValidateCatalogFile(path));
        Assert.Contains("notice minimumBytes is outside the allowed range", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Owner_accepted_record_with_no_blockers_can_cross_publication_boundary()
    {
        var recordPath = CreateRecord(
            legalClearance: true,
            [],
            "accepted-by-project-owner",
            "Terion Labs project owner",
            "2026-08-25T12:00:00Z");

        var inspection = MacOsReleaseLegalClosure.Validate(
            recordPath,
            _temporaryDirectory);

        MacOsReleaseLegalClosure.RequirePublicationClearance(inspection);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Clearance_and_blockers_must_describe_a_consistent_decision(
        bool legalClearance,
        bool includeBlocker)
    {
        var blockers = includeBlocker
            ? new[] { "Review remains open." }
            : [];
        var recordPath = CreateRecord(
            legalClearance,
            blockers,
            legalClearance
                ? "accepted-by-project-owner"
                : "pending-project-owner-decision",
            legalClearance ? "Terion Labs project owner" : null,
            legalClearance ? "2026-08-25T12:00:00Z" : null);

        Assert.Throws<InvalidDataException>(() =>
            MacOsReleaseLegalClosure.Validate(
                recordPath,
                _temporaryDirectory));
    }

    [Fact]
    public void Record_rejects_changed_bound_evidence()
    {
        var recordPath = CreateRecord(
            legalClearance: false,
            ["The project owner has not made a release decision."],
            "pending-project-owner-decision",
            reviewedBy: null,
            reviewedAtUtc: null);
        File.AppendAllText(
            Path.Combine(_temporaryDirectory, "licenses", "managed-components.json"),
            "changed");

        Assert.Throws<InvalidDataException>(() =>
            MacOsReleaseLegalClosure.Validate(
                recordPath,
                _temporaryDirectory));
    }

    [Fact]
    public void Clearance_rejects_a_pending_nested_evidence_disposition()
    {
        var recordPath = CreateRecord(
            legalClearance: true,
            [],
            "accepted-by-project-owner",
            "Terion Labs project owner",
            "2026-08-25T12:00:00Z");
        var record = JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();
        record["dispositions"]!["cefMacos"]!["status"] =
            "pending-project-owner-decision";
        File.WriteAllText(recordPath, record.ToJsonString());

        Assert.Throws<InvalidDataException>(() =>
            MacOsReleaseLegalClosure.Validate(
                recordPath,
                _temporaryDirectory));
    }

    [Fact]
    public void Owner_clearance_rejects_an_ambiguous_review_basis()
    {
        var recordPath = CreateRecord(
            legalClearance: true,
            [],
            "accepted-by-project-owner",
            "Terion Labs project owner",
            "2026-08-25T12:00:00Z");
        var record = JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();
        record["review"]!["basis"] = "approved";
        File.WriteAllText(recordPath, record.ToJsonString());

        Assert.Throws<InvalidDataException>(() =>
            MacOsReleaseLegalClosure.Validate(
                recordPath,
                _temporaryDirectory));
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string CreateRecord(
        bool legalClearance,
        IReadOnlyList<string> releaseBlockers,
        string status,
        string? reviewedBy,
        string? reviewedAtUtc)
    {
        var evidence = EvidencePaths.Select(relativePath =>
        {
            var path = Path.Combine(
                _temporaryDirectory,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"fixture {relativePath}");
            return new
            {
                path = relativePath,
                sha256 = Sha256(path),
            };
        });
        var record = new
        {
            schemaVersion = 1,
            format = "asura-macos-release-legal-closure-v1",
            platform = "macos-arm64",
            legalClearance,
            releaseBlockers,
            excludedPlatforms = new[] { "windows", "linux" },
            review = new
            {
                status,
                basis = legalClearance
                    ? "documented-engineering-evidence-without-independent-legal-review"
                    : null,
                reviewedBy,
                reviewedAtUtc,
            },
            dispositions = Dispositions(legalClearance),
            evidence,
        };
        var recordPath = Path.Combine(_temporaryDirectory, "legal-record.json");
        File.WriteAllText(recordPath, JsonSerializer.Serialize(record));
        return recordPath;
    }

    private static object Dispositions(bool approved) => new
    {
        managedComponents = Disposition(approved, "managed fixture"),
        nativeTerminalAndShell = Disposition(approved, "terminal fixture"),
        cefMacos = Disposition(approved, "CEF fixture"),
        sqlLanguageWorker = Disposition(approved, "SQL fixture"),
    };

    private static object Disposition(bool approved, string comment) => new
    {
        status = approved
            ? "accepted-by-owner-for-macos"
            : "pending-project-owner-decision",
        scope = "macos-arm64",
        comment,
    };

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Asura.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}

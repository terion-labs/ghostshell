using Asura.AccessibilityAcceptance;

namespace Asura.SoakAcceptance.Tests;

public sealed class SoakAcceptanceTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"asura-soak-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Catalog_covers_every_required_release_scenario()
    {
        Assert.Equal(10, SoakCatalog.Scenarios.Count);
        Assert.Equal(
            [
                "reconnect-reattach",
                "startup-crash-restore",
                "many-tabs-panels",
                "bounded-scrollback",
                "provider-failure-noncooperation",
                "cef-renderer-replacement",
                "mcp-failure-cleanup",
                "sleep-wake",
                "quick-terminal-cycles",
                "native-view-open-close",
            ],
            SoakCatalog.Scenarios.Select(scenario => scenario.Id),
            StringComparer.Ordinal);
        Assert.Equal(64, SoakCatalog.Sha256.Length);
        Assert.Single(SoakCatalog.Scenarios, scenario => scenario.ExpectedAbruptExits == 1);
    }

    [Fact]
    public void Checked_in_v1_policy_is_concrete_and_complete()
    {
        var loaded = SoakPolicyFiles.Load(Path.Combine(AppContext.BaseDirectory, "policy.v1.json"));

        Assert.Equal("release-v1", loaded.Policy.PolicyVersion);
        Assert.Equal(SoakCatalog.Scenarios.Count, loaded.Policy.Scenarios.Count);
        Assert.All(loaded.Policy.Scenarios, budget =>
        {
            Assert.True(budget.DurationSeconds >= 900);
            Assert.True(budget.RequiredLoad > 0);
            Assert.True(budget.MaximumWorkingSetGrowthBytes > 0);
            Assert.Equal(0, budget.MaximumFailures);
            Assert.InRange(budget.CleanupTimeoutSeconds, 30, 60);
            Assert.Equal(0, budget.MaximumLiveProcessesAfterCleanup);
        });
    }

    [Fact]
    public void Policy_rejects_reordered_catalog()
    {
        var loaded = SoakPolicyFiles.Load(Path.Combine(AppContext.BaseDirectory, "policy.v1.json"));
        var reordered = loaded.Policy with
        {
            Scenarios = [.. loaded.Policy.Scenarios.Reverse()],
        };

        Assert.Throws<InvalidDataException>(() => SoakPolicyFiles.Validate(reordered));
    }

    [Fact]
    public void Evaluator_fails_each_machine_budget()
    {
        var scenario = SoakCatalog.Scenarios[0];
        var budget = new ScenarioBudget(scenario.Id, 60, 10, scenario.LoadUnit, 100, 0, 5, 0);
        var resources = new ResourceObservation(1, 10, 200, 120, 110, 1, 2, 2);
        var codes = new List<string>();

        Program.EvaluateBudget(scenario, budget, 9, 1, 1, cleanupPassed: false, resources, codes);

        Assert.Equal(
            [
                "required-load-not-met",
                "failure-budget-exceeded",
                "unexpected-exit-count",
                "rss-growth-budget-exceeded",
                "cleanup-invariant-failed",
            ],
            codes);
    }

    [Fact]
    public void Receipt_writer_emits_exact_privacy_safe_triplet()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var loaded = SoakPolicyFiles.Load(Path.Combine(AppContext.BaseDirectory, "policy.v1.json"));
        var observations = SoakCatalog.Scenarios.Select(scenario => new ScenarioObservation(
            scenario.Id,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            loaded.Policy.Scenarios.Single(budget => budget.Id == scenario.Id).RequiredLoad,
            0,
            scenario.ExpectedAbruptExits,
            SoakStatus.Pass,
            SoakStatus.Pass,
            [],
            new ResourceObservation(2, 100, 120, 110, 10, 5, 2, 2),
            true)).ToArray();
        var receipt = new SoakReceipt(
            1,
            "asura-macos-arm64-release-soak",
            "1.0.0",
            SoakCatalog.Sha256,
            loaded.Sha256,
            loaded.Policy,
            new SoakHost("macos-arm64-reference-v1", "host-0123456789abcdef", "macOS 15.0", "Arm64", "Arm64", 8, "ac"),
            new BuildIdentity("release-v1", "macos-app-bundle", "Asura", "1.0.0", 10, new string('a', 64), 2, new string('b', 64), "sh.asura"),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddHours(1),
            SoakStatus.Pass,
            true,
            observations);

        var paths = SoakEvidenceFiles.Write(_temporaryDirectory, receipt);

        Assert.Equal(3, Directory.EnumerateFiles(paths.Directory).Count());
        Assert.EndsWith("receipt.json", paths.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, File.ReadAllText(paths.Markdown), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}

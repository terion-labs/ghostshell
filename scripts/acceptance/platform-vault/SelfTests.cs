using System.Text.Json;
using System.Text.Json.Nodes;

namespace Asura.PlatformVaultAcceptance;

internal static class SelfTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run()
    {
        var failures = new List<string>();
        Run("schema parses and forbids extra root fields", SchemaIsStrict, failures);
        Run("valid PASS receipt is accepted", ValidPassIsAccepted, failures);
        Run("valid BLOCKED receipt is accepted", ValidBlockedIsAccepted, failures);
        Run("valid cleanup-recovery FAIL is accepted", ValidRecoveryFailureIsAccepted, failures);
        Run("valid runner-recovery FAIL is accepted", ValidRunnerRecoveryFailureIsAccepted, failures);
        Run("raw output field is rejected", RawOutputIsRejected, failures);
        Run("duplicate properties are rejected", DuplicatePropertyIsRejected, failures);
        Run("PASS with nonzero exit is rejected", InvalidPassIsRejected, failures);
        Run("PASS on an unsupported host is rejected", UnsupportedPassIsRejected, failures);
        Run("recovery identifiers require recovery state", StrayRecoveryIsRejected, failures);
        Run("recovery state requires identifiers", MissingRecoveryIsRejected, failures);
        Run("recovery directory must match its run", MismatchedRecoveryDirectoryIsRejected, failures);
        Run("TRX for another test is rejected", DifferentTrxTestIsRejected, failures);
        Run("TRX with additional tests is rejected", MultiTestTrxIsRejected, failures);
        Run("isolated run writes recovery manifest before execution", IsolatedRunWritesRecoveryManifest, failures);
        Run("interrupted created item requires recovery", InterruptedItemRequiresRecovery, failures);
        Run("deleted item confirms cleanup", DeletedItemConfirmsCleanup, failures);

        if (failures.Count == 0)
        {
            Console.WriteLine("Platform-vault acceptance self-tests passed (17/17).");
            return 0;
        }

        foreach (var failure in failures)
        {
            Console.Error.WriteLine(failure);
        }

        return 1;
    }

    private static void SchemaIsStrict()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "receipt.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert(root.GetProperty("$schema").GetString() == "https://json-schema.org/draft/2020-12/schema");
        Assert(root.GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
        Assert(root.GetProperty("required").GetArrayLength() == 11);
    }

    private static void ValidPassIsAccepted() => Assert(ReceiptValidator.Validate(ValidPassJson()).Count == 0);

    private static void ValidBlockedIsAccepted()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["status"] = "BLOCKED";
        receipt["reason"] = "provider_prerequisite_missing";
        receipt["test"]!["outcome"] = "NOT_RUN";
        receipt["test"]!["exitCode"] = null;
        receipt["test"]!["durationMilliseconds"] = 0;
        receipt["cleanup"]!["state"] = "NOT_STARTED";
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count == 0);
    }

    private static void ValidRecoveryFailureIsAccepted()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["status"] = "FAIL";
        receipt["reason"] = "test_failed";
        receipt["test"]!["outcome"] = "FAILED";
        receipt["test"]!["exitCode"] = 1;
        receipt["cleanup"]!["state"] = "RECOVERY_REQUIRED";
        receipt["cleanup"]!["recovery"] = JsonSerializer.SerializeToNode(
            new RecoveryReceipt(
                "sh.asura.integration-tests.0123456789abcdef0123456789abcdef",
                "abcdef0123456789abcdef0123456789",
                "/tmp/asura-platform-vault-0123456789abcdef0123456789abcdef/metadata"),
            JsonOptions);
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count == 0);
    }

    private static void ValidRunnerRecoveryFailureIsAccepted()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["status"] = "FAIL";
        receipt["reason"] = "runner_failed";
        receipt["test"]!["outcome"] = "NOT_RUN";
        receipt["test"]!["exitCode"] = null;
        receipt["test"]!["durationMilliseconds"] = 0;
        receipt["cleanup"]!["state"] = "RECOVERY_REQUIRED";
        receipt["cleanup"]!["recovery"] = JsonSerializer.SerializeToNode(
            new RecoveryReceipt(
                "sh.asura.integration-tests.0123456789abcdef0123456789abcdef",
                "abcdef0123456789abcdef0123456789",
                "/tmp/asura-platform-vault-0123456789abcdef0123456789abcdef/metadata"),
            JsonOptions);
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count == 0);
    }

    private static void RawOutputIsRejected()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["rawOutput"] = "must never be durable";
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count != 0);
    }

    private static void DuplicatePropertyIsRejected()
    {
        var receipt = ValidPassJson().Replace(
            "\"status\": \"PASS\",",
            "\"status\": \"PASS\", \"status\": \"PASS\",",
            StringComparison.Ordinal);
        Assert(ReceiptValidator.Validate(receipt).Count != 0);
    }

    private static void InvalidPassIsRejected()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["test"]!["exitCode"] = 1;
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count != 0);
    }

    private static void UnsupportedPassIsRejected()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["host"]!["osFamily"] = "Unsupported";
        receipt["provider"]!["adapter"] = "unavailable";
        receipt["provider"]!["persistence"] = "none";
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count != 0);
    }

    private static void StrayRecoveryIsRejected()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["cleanup"]!["recovery"] = JsonSerializer.SerializeToNode(
            new RecoveryReceipt(
                "sh.asura.integration-tests.0123456789abcdef0123456789abcdef",
                "abcdef0123456789abcdef0123456789",
                "/tmp/asura-platform-vault-0123456789abcdef0123456789abcdef/metadata"),
            JsonOptions);
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count != 0);
    }

    private static void MissingRecoveryIsRejected()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["status"] = "FAIL";
        receipt["reason"] = "cleanup_unconfirmed";
        receipt["test"]!["outcome"] = "FAILED";
        receipt["test"]!["exitCode"] = 1;
        receipt["cleanup"]!["state"] = "RECOVERY_REQUIRED";
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count != 0);
    }

    private static void MismatchedRecoveryDirectoryIsRejected()
    {
        var receipt = JsonNode.Parse(ValidPassJson())!.AsObject();
        receipt["status"] = "FAIL";
        receipt["reason"] = "cleanup_unconfirmed";
        receipt["test"]!["outcome"] = "FAILED";
        receipt["test"]!["exitCode"] = 1;
        receipt["cleanup"]!["state"] = "RECOVERY_REQUIRED";
        receipt["cleanup"]!["recovery"] = JsonSerializer.SerializeToNode(
            new RecoveryReceipt(
                "sh.asura.integration-tests.0123456789abcdef0123456789abcdef",
                "abcdef0123456789abcdef0123456789",
                "/tmp/not-the-isolated-run/metadata"),
            JsonOptions);
        Assert(ReceiptValidator.Validate(receipt.ToJsonString()).Count != 0);
    }

    private static void DifferentTrxTestIsRejected()
    {
        const string trx =
            "<TestRun><Results><UnitTestResult testName=\"Other.Test\" outcome=\"Passed\" duration=\"00:00:00.001\" /></Results></TestRun>";
        Assert(ReadTrx(trx).Outcome == "NOT_RUN");
    }

    private static void MultiTestTrxIsRejected()
    {
        var trx =
            $"<TestRun><Results><UnitTestResult testName=\"{AcceptanceRunner.TestId}\" outcome=\"Passed\" duration=\"00:00:00.001\" />"
            + "<UnitTestResult testName=\"Other.Test\" outcome=\"Passed\" duration=\"00:00:00.001\" /></Results></TestRun>";
        Assert(ReadTrx(trx).Outcome == "NOT_RUN");
    }

    private static AcceptanceRunner.TrxResult ReadTrx(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"asura-platform-vault-trx-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(path, content);
            return AcceptanceRunner.ReadTrxResult(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void IsolatedRunWritesRecoveryManifest()
    {
        var run = IsolatedVaultRun.Create();
        try
        {
            var path = Path.Combine(run.RootPath, IsolatedVaultRun.RecoveryManifestFileName);
            var manifest = JsonSerializer.Deserialize<RecoveryReceipt>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert(manifest == new RecoveryReceipt(
                run.ServiceName,
                run.SecretReference,
                run.MetadataPath));
        }
        finally
        {
            Directory.Delete(run.RootPath, recursive: true);
        }
    }

    private static void InterruptedItemRequiresRecovery()
    {
        var run = SyntheticRun();
        var cleanup = AcceptanceRunner.ResolveCleanup(
            "FAILED",
            new AcceptanceState(1, run.RunId, "CREATED"),
            run);
        Assert(cleanup == new CleanupReceipt(
            "RECOVERY_REQUIRED",
            new RecoveryReceipt(run.ServiceName, run.SecretReference, run.MetadataPath)));
    }

    private static void DeletedItemConfirmsCleanup()
    {
        var run = SyntheticRun();
        var cleanup = AcceptanceRunner.ResolveCleanup(
            "FAILED",
            new AcceptanceState(1, run.RunId, "DELETED"),
            run);
        Assert(cleanup == new CleanupReceipt("CONFIRMED", null));
    }

    private static IsolatedVaultRun SyntheticRun() => new(
        "0123456789abcdef0123456789abcdef",
        "abcdef0123456789abcdef0123456789",
        "/tmp/asura-platform-vault-test",
        "/tmp/asura-platform-vault-test/metadata",
        "/tmp/asura-platform-vault-test/state.json",
        "sh.asura.integration-tests.0123456789abcdef0123456789abcdef");

    private static string ValidPassJson()
    {
        var receipt = new AcceptanceReceipt(
            1,
            "PASS",
            "accepted",
            "2026-07-23T00:00:00.0000000+00:00",
            "2026-07-23T00:00:01.0000000+00:00",
            1000,
            new HostReceipt("macOS", "Darwin 25.5.0 Darwin Kernel Version", "arm64"),
            new DotnetReceipt("10.0.302"),
            new ProviderReceipt("macos-keychain", "os-protected-persistent"),
            new TestReceipt(AcceptanceRunner.TestId, "PASSED", 0, 135),
            new CleanupReceipt("CONFIRMED", null));
        return JsonSerializer.Serialize(receipt, JsonOptions);
    }

    private static void Run(string name, Action test, List<string> failures)
    {
        try
        {
            test();
        }
        catch (Exception exception)
        {
            failures.Add($"FAIL: {name} ({exception.GetType().Name})");
        }
    }

    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Self-test assertion failed.");
        }
    }
}

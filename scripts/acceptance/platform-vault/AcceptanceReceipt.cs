using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Asura.PlatformVaultAcceptance;

internal sealed record AcceptanceReceipt(
    int SchemaVersion,
    string Status,
    string Reason,
    string StartedAtUtc,
    string CompletedAtUtc,
    long DurationMilliseconds,
    HostReceipt Host,
    DotnetReceipt Dotnet,
    ProviderReceipt Provider,
    TestReceipt Test,
    CleanupReceipt Cleanup);

internal sealed record HostReceipt(
    string OsFamily,
    string OsDescription,
    string Architecture);

internal sealed record DotnetReceipt(string? SdkVersion);

internal sealed record ProviderReceipt(
    string Adapter,
    string Persistence);

internal sealed record TestReceipt(
    string TestId,
    string Outcome,
    int? ExitCode,
    long DurationMilliseconds);

internal sealed record CleanupReceipt(
    string State,
    RecoveryReceipt? Recovery);

internal sealed record RecoveryReceipt(
    string ServiceName,
    string SecretReference,
    string MetadataDirectory);

internal static partial class ReceiptValidator
{
    private static readonly string[] RootProperties =
    [
        "schemaVersion",
        "status",
        "reason",
        "startedAtUtc",
        "completedAtUtc",
        "durationMilliseconds",
        "host",
        "dotnet",
        "provider",
        "test",
        "cleanup",
    ];

    public static IReadOnlyList<string> Validate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return Validate(document.RootElement);
        }
        catch (JsonException)
        {
            return ["Receipt is not valid JSON."];
        }
    }

    private static IReadOnlyList<string> Validate(JsonElement root)
    {
        var errors = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ["Receipt root must be an object."];
        }

        RequireExactProperties(root, RootProperties, "$", errors);
        RequireInteger(root, "schemaVersion", 1, 1, errors);
        var status = RequireEnum(root, "status", ["PASS", "FAIL", "BLOCKED"], errors);
        var reason = RequireEnum(
            root,
            "reason",
            [
                "accepted",
                "provider_prerequisite_missing",
                "unsupported_platform",
                "dotnet_unavailable",
                "test_failed",
                "test_skipped",
                "test_execution_failed",
                "cleanup_unconfirmed",
                "runner_failed",
            ],
            errors);
        var startedAt = RequireUtcTimestamp(root, "startedAtUtc", errors);
        var completedAt = RequireUtcTimestamp(root, "completedAtUtc", errors);
        if (startedAt is not null && completedAt is not null && completedAt < startedAt)
        {
            errors.Add("$.completedAtUtc must not precede $.startedAtUtc.");
        }

        RequireInteger(root, "durationMilliseconds", 0, long.MaxValue, errors);

        var host = RequireObject(root, "host", errors);
        string? osFamily = null;
        if (host is { } hostValue)
        {
            RequireExactProperties(hostValue, ["osFamily", "osDescription", "architecture"], "$.host", errors);
            osFamily = RequireEnum(hostValue, "osFamily", ["macOS", "Windows", "Linux", "Unsupported"], errors);
            RequireSafeString(hostValue, "osDescription", 1, 256, errors);
            RequireEnum(hostValue, "architecture", ["x64", "arm64", "x86", "arm", "unknown"], errors);
        }

        var dotnet = RequireObject(root, "dotnet", errors);
        string? sdkVersion = null;
        if (dotnet is { } dotnetValue)
        {
            RequireExactProperties(dotnetValue, ["sdkVersion"], "$.dotnet", errors);
            sdkVersion = RequireNullableSafeString(dotnetValue, "sdkVersion", 1, 64, errors);
            if (sdkVersion is not null && !DotnetVersionPattern().IsMatch(sdkVersion))
            {
                errors.Add("$.dotnet.sdkVersion is not a sanitized .NET SDK version.");
            }
        }

        var provider = RequireObject(root, "provider", errors);
        string? adapter = null;
        string? persistence = null;
        if (provider is { } providerValue)
        {
            RequireExactProperties(providerValue, ["adapter", "persistence"], "$.provider", errors);
            adapter = RequireEnum(
                providerValue,
                "adapter",
                ["macos-keychain", "windows-dpapi", "linux-secret-service", "unavailable"],
                errors);
            persistence = RequireEnum(providerValue, "persistence", ["os-protected-persistent", "none"], errors);
        }

        var test = RequireObject(root, "test", errors);
        string? testOutcome = null;
        int? exitCode = null;
        if (test is { } testValue)
        {
            RequireExactProperties(
                testValue,
                ["testId", "outcome", "exitCode", "durationMilliseconds"],
                "$.test",
                errors);
            var testId = RequireSafeString(testValue, "testId", 1, 256, errors);
            if (testId is not null && !string.Equals(testId, AcceptanceRunner.TestId, StringComparison.Ordinal))
            {
                errors.Add("$.test.testId is not the native-vault acceptance case.");
            }

            testOutcome = RequireEnum(testValue, "outcome", ["PASSED", "FAILED", "SKIPPED", "NOT_RUN"], errors);
            exitCode = RequireNullableInteger(testValue, "exitCode", errors);
            RequireInteger(testValue, "durationMilliseconds", 0, long.MaxValue, errors);
        }

        var cleanup = RequireObject(root, "cleanup", errors);
        string? cleanupState = null;
        bool hasRecovery = false;
        if (cleanup is { } cleanupValue)
        {
            RequireExactProperties(cleanupValue, ["state", "recovery"], "$.cleanup", errors);
            cleanupState = RequireEnum(
                cleanupValue,
                "state",
                ["CONFIRMED", "NOT_STARTED", "RECOVERY_REQUIRED"],
                errors);
            hasRecovery = ValidateRecovery(cleanupValue, errors);
        }

        ValidateInvariants(
            status,
            reason,
            osFamily,
            sdkVersion,
            adapter,
            persistence,
            testOutcome,
            exitCode,
            cleanupState,
            hasRecovery,
            errors);
        return errors;
    }

    private static bool ValidateRecovery(JsonElement cleanup, List<string> errors)
    {
        if (!cleanup.TryGetProperty("recovery", out var recovery))
        {
            errors.Add("$.cleanup.recovery is required.");
            return false;
        }

        if (recovery.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (recovery.ValueKind != JsonValueKind.Object)
        {
            errors.Add("$.cleanup.recovery must be an object or null.");
            return false;
        }

        RequireExactProperties(
            recovery,
            ["serviceName", "secretReference", "metadataDirectory"],
            "$.cleanup.recovery",
            errors);
        var serviceName = RequireSafeString(recovery, "serviceName", 1, 128, errors);
        var secretReference = RequireSafeString(recovery, "secretReference", 1, 64, errors);
        var metadataDirectory = RequireSafeString(recovery, "metadataDirectory", 1, 1024, errors);
        if (serviceName is not null && !ServiceNamePattern().IsMatch(serviceName))
        {
            errors.Add("$.cleanup.recovery.serviceName is not an isolated acceptance namespace.");
        }
        else if (serviceName is not null
                 && metadataDirectory is not null
                 && !IsMatchingIsolatedMetadataDirectory(serviceName, metadataDirectory))
        {
            errors.Add("$.cleanup.recovery.metadataDirectory does not match the isolated acceptance run.");
        }

        if (secretReference is not null && !OpaqueIdPattern().IsMatch(secretReference))
        {
            errors.Add("$.cleanup.recovery.secretReference is not an opaque acceptance reference.");
        }

        return true;
    }

    private static void ValidateInvariants(
        string? status,
        string? reason,
        string? osFamily,
        string? sdkVersion,
        string? adapter,
        string? persistence,
        string? testOutcome,
        int? exitCode,
        string? cleanupState,
        bool hasRecovery,
        List<string> errors)
    {
        if (status == "PASS"
            && (reason != "accepted" || testOutcome != "PASSED" || exitCode != 0 || cleanupState != "CONFIRMED"))
        {
            errors.Add("A PASS receipt requires an accepted, passed test with exit code 0 and confirmed cleanup.");
        }

        if (status == "PASS"
            && (osFamily is not ("macOS" or "Windows" or "Linux")
                || adapter == "unavailable"
                || persistence != "os-protected-persistent"))
        {
            errors.Add("A PASS receipt requires a supported host and an OS-protected persistent provider.");
        }

        if (status == "BLOCKED"
            && (testOutcome is not ("NOT_RUN" or "SKIPPED") || cleanupState != "NOT_STARTED"))
        {
            errors.Add("A BLOCKED receipt requires a not-run/skipped test and no started cleanup lifecycle.");
        }

        if (cleanupState == "RECOVERY_REQUIRED" && (status != "FAIL" || !hasRecovery))
        {
            errors.Add("RECOVERY_REQUIRED is valid only for FAIL and requires recovery identifiers.");
        }

        if (cleanupState != "RECOVERY_REQUIRED" && hasRecovery)
        {
            errors.Add("Recovery identifiers are forbidden unless recovery is required.");
        }

        if (status != "PASS" && reason == "accepted")
        {
            errors.Add("Only PASS may use the accepted reason.");
        }

        var blockedReason = reason is "provider_prerequisite_missing"
            or "unsupported_platform"
            or "dotnet_unavailable"
            or "test_skipped";
        if (status == "BLOCKED" && !blockedReason)
        {
            errors.Add("BLOCKED requires a blocked-environment reason.");
        }

        var failureReason = reason is "test_failed"
            or "test_execution_failed"
            or "cleanup_unconfirmed"
            or "runner_failed";
        if (status == "FAIL" && !failureReason)
        {
            errors.Add("FAIL requires a runner, execution, lifecycle, or cleanup failure reason.");
        }

        var testExecuted = testOutcome is "PASSED" or "FAILED" or "SKIPPED";
        if (testExecuted && exitCode is null)
        {
            errors.Add("An executed test outcome requires a process exit code.");
        }

        if (testOutcome == "NOT_RUN" && exitCode is not null)
        {
            errors.Add("A not-run test cannot have a process exit code.");
        }

        if (testExecuted && sdkVersion is null)
        {
            errors.Add("An executed test requires a sanitized .NET SDK version.");
        }

        if (cleanupState == "NOT_STARTED" && testOutcome is ("PASSED" or "FAILED"))
        {
            errors.Add("An executed lifecycle cannot report cleanup as not started.");
        }

        var expectedAdapter = osFamily switch
        {
            "macOS" => "macos-keychain",
            "Windows" => "windows-dpapi",
            "Linux" => "linux-secret-service",
            "Unsupported" => "unavailable",
            _ => null,
        };
        if (expectedAdapter is not null && adapter != expectedAdapter)
        {
            errors.Add("Provider adapter does not match the receipt OS family.");
        }

        var expectedPersistence = adapter == "unavailable" ? "none" : "os-protected-persistent";
        if (adapter is not null && persistence != expectedPersistence)
        {
            errors.Add("Provider persistence does not match the adapter.");
        }
    }

    private static JsonElement? RequireObject(JsonElement parent, string name, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"$.{name} must be an object.");
            return null;
        }

        return value;
    }

    private static string? RequireEnum(
        JsonElement parent,
        string name,
        IReadOnlyCollection<string> allowed,
        List<string> errors)
    {
        var value = RequireSafeString(parent, name, 1, 64, errors);
        if (value is not null && !allowed.Contains(value, StringComparer.Ordinal))
        {
            errors.Add($"$.{name} has an unsupported value.");
        }

        return value;
    }

    private static string? RequireSafeString(
        JsonElement parent,
        string name,
        int minimumLength,
        int maximumLength,
        List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            errors.Add($"$.{name} must be a string.");
            return null;
        }

        var value = property.GetString()!;
        var lengthIsValid = value.Length >= minimumLength && value.Length <= maximumLength;
        if (!lengthIsValid || value.Any(char.IsControl))
        {
            errors.Add($"$.{name} is not a safe bounded string.");
        }

        return value;
    }

    private static string? RequireNullableSafeString(
        JsonElement parent,
        string name,
        int minimumLength,
        int maximumLength,
        List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var property))
        {
            errors.Add($"$.{name} is required.");
            return null;
        }

        return property.ValueKind == JsonValueKind.Null
            ? null
            : RequireSafeString(parent, name, minimumLength, maximumLength, errors);
    }

    private static void RequireInteger(
        JsonElement parent,
        string name,
        long minimum,
        long maximum,
        List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number)
            || number < minimum
            || number > maximum)
        {
            errors.Add($"$.{name} must be an integer from {minimum} through {maximum}.");
        }
    }

    private static int? RequireNullableInteger(JsonElement parent, string name, List<string> errors)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            errors.Add($"$.{name} is required.");
            return null;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            errors.Add($"$.{name} must be an integer or null.");
            return null;
        }

        return number;
    }

    private static DateTimeOffset? RequireUtcTimestamp(JsonElement parent, string name, List<string> errors)
    {
        var value = RequireSafeString(parent, name, 1, 64, errors);
        if (value is null
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp)
            || timestamp.Offset != TimeSpan.Zero)
        {
            errors.Add($"$.{name} must be an ISO-8601 UTC timestamp.");
            return null;
        }

        return timestamp;
    }

    private static void RequireExactProperties(
        JsonElement value,
        IReadOnlyCollection<string> expected,
        string path,
        List<string> errors)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        foreach (var duplicate in actual
                     .GroupBy(name => name, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add($"{path}.{duplicate.Key} occurs more than once.");
        }

        foreach (var missing in expected.Except(actual, StringComparer.Ordinal))
        {
            errors.Add($"{path}.{missing} is required.");
        }

        foreach (var unexpected in actual.Except(expected, StringComparer.Ordinal))
        {
            errors.Add($"{path}.{unexpected} is not allowed.");
        }
    }

    private static bool IsMatchingIsolatedMetadataDirectory(
        string serviceName,
        string metadataDirectory)
    {
        var runId = serviceName[(serviceName.LastIndexOf('.') + 1)..];
        var normalized = metadataDirectory.Replace('\\', '/');
        var isUnixAbsolute = normalized.StartsWith("/", StringComparison.Ordinal);
        var isWindowsAbsolute = normalized.Length >= 3
            && char.IsAsciiLetter(normalized[0])
            && normalized[1] == ':'
            && normalized[2] == '/';
        return (isUnixAbsolute || isWindowsAbsolute)
            && normalized.EndsWith(
                $"/asura-platform-vault-{runId}/metadata",
                StringComparison.Ordinal)
            && !normalized.Split('/').Contains("..", StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetVersionPattern();

    [GeneratedRegex(@"^app\.asura\.integration-tests\.[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNamePattern();

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueIdPattern();
}

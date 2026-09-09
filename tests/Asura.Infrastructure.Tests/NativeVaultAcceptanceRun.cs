using System.Text.Json;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

internal sealed record NativeVaultAcceptanceRun(
    string RunId,
    SecretRef Reference,
    DirectoryInfo DataDirectory,
    string StatePath)
{
    private const string RunIdEnvironmentVariable = "ASURA_SECRET_VAULT_ACCEPTANCE_RUN_ID";
    private const string SecretReferenceEnvironmentVariable = "ASURA_SECRET_VAULT_ACCEPTANCE_REFERENCE";
    private const string RootEnvironmentVariable = "ASURA_SECRET_VAULT_ACCEPTANCE_ROOT";

    public static NativeVaultAcceptanceRun? FromEnvironment()
    {
        var runId = Environment.GetEnvironmentVariable(RunIdEnvironmentVariable);
        var secretReference = Environment.GetEnvironmentVariable(SecretReferenceEnvironmentVariable);
        var root = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (runId is null && secretReference is null && root is null)
        {
            return null;
        }

        if (!IsOpaqueId(runId) || !IsOpaqueId(secretReference) || string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("Native-vault acceptance environment is incomplete or invalid.");
        }

        var expectedRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), $"asura-platform-vault-{runId}"));
        var actualRoot = Path.GetFullPath(root);
        if (!string.Equals(expectedRoot, actualRoot, PathComparison))
        {
            throw new InvalidOperationException("Native-vault acceptance root is outside its isolated temporary directory.");
        }

        Directory.CreateDirectory(actualRoot);
        return new NativeVaultAcceptanceRun(
            runId!,
            new SecretRef(secretReference!),
            new DirectoryInfo(Path.Combine(actualRoot, "metadata")),
            Path.Combine(actualRoot, "state.json"));
    }

    public void Record(string phase)
    {
        if (phase is not ("INITIALIZED" or "CREATED" or "DELETED" or "CLEANUP_FAILED"))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        var json = JsonSerializer.Serialize(new AcceptanceState(1, RunId, phase));
        var temporaryPath = $"{StatePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, StatePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsOpaqueId(string? value) =>
        value is { Length: 32 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record AcceptanceState(int SchemaVersion, string RunId, string Phase);
}

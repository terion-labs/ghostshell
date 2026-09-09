using Asura.Application;
using Asura.Core;

namespace Asura.Infrastructure.Tests;

internal static class ConnectionRuntimeTestSupport
{
    public static ConnectionProfile Profile(
        ConnectionEndpoint endpoint,
        ConnectionAuthentication? authentication = null,
        ConnectionStartup? startup = null,
        ConnectionKeepAlive? keepAlive = null,
        SshHostKeyPolicy? hostKeyPolicy = null,
        string id = "test-connection") =>
        new(
            new ConnectionId(id),
            ConnectionProfile.CurrentSchemaVersion,
            "Test connection",
            endpoint,
            authentication ?? new ConnectionAuthentication.None(),
            startup ?? ConnectionStartup.Default,
            keepAlive ?? ConnectionKeepAlive.Disabled,
            hostKeyPolicy ?? (endpoint is ConnectionEndpoint.Ssh
                ? SshHostKeyPolicy.Strict
                : SshHostKeyPolicy.NotApplicable));

    public static T Success<T>(ConnectionRuntimeResult<T> result) =>
        Assert.IsType<ConnectionRuntimeResult<T>.Success>(result).Value;

    public static ConnectionRuntimeError Failure<T>(ConnectionRuntimeResult<T> result) =>
        Assert.IsType<ConnectionRuntimeResult<T>.Failure>(result).Error;
}

internal sealed class RecordingExecutableLocator : IConnectionExecutableLocator
{
    private readonly Dictionary<string, string> _executables = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = [];

    public void Add(string name, string path) => _executables[name] = path;

    public string? Find(string executable)
    {
        Requests.Add(executable);
        return _executables.GetValueOrDefault(executable);
    }
}

internal sealed class RecordingCommandRunner : IConnectionCommandRunner
{
    public ConnectionProbeResult Result { get; set; } = ConnectionProbeResult.Success;

    public List<ConnectionProbeCommand> Commands { get; } = [];

    public ValueTask<ConnectionProbeResult> RunAsync(
        ConnectionProbeCommand command,
        CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return ValueTask.FromResult(cancellationToken.IsCancellationRequested
            ? new ConnectionProbeResult(
                ConnectionProbeOutcome.Cancelled,
                null,
                string.Empty)
            : Result);
    }
}

internal sealed class RecordingSecretVault : ISecretVault
{
    private readonly Dictionary<SecretRef, SecretScope> _entries = [];

    public SecretVaultErrorCode? ForcedError { get; set; }

    public List<ResolveSecretRequest> ResolveRequests { get; } = [];

    public List<GetSecretMetadataRequest> MetadataRequests { get; } = [];

    public SecretMaterial? LastMaterial { get; private set; }

    public SecretVaultAvailability Availability { get; } = new(
        SecretVaultAvailabilityState.Available,
        SecretVaultPersistenceKind.MemoryOnly,
        SecretVaultCapabilities.All,
        "test",
        "test",
        "Test vault.");

    public void Add(SecretRef reference, string connectionId) =>
        _entries.Add(
            reference,
            new SecretScope(SecretScopeKind.Connection, connectionId));

    public ValueTask<SecretVaultResult<SecretMaterial>> ResolveAsync(
        ResolveSecretRequest request,
        CancellationToken cancellationToken)
    {
        ResolveRequests.Add(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.Cancelled)));
        }

        if (ForcedError is { } forcedError)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Fail(
                SecretVaultError.Create(forcedError)));
        }

        if (!_entries.TryGetValue(request.Reference, out var storedScope))
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.NotFound)));
        }

        if (storedScope != request.Scope)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.AccessDenied)));
        }

        LastMaterial = SecretMaterial.CopyFrom("do-not-leak"u8);
        return ValueTask.FromResult(SecretVaultResult<SecretMaterial>.Succeed(LastMaterial));
    }

    public ValueTask<SecretVaultResult<SecretMetadata>> CreateAsync(
        CreateSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> ReplaceAsync(
        ReplaceSecretRequest request,
        SecretMaterial material,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> RelabelAsync(
        RelabelSecretRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<Unit>> DeleteAsync(
        DeleteSecretRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SecretVaultResult<SecretMetadata>> GetMetadataAsync(
        GetSecretMetadataRequest request,
        CancellationToken cancellationToken)
    {
        MetadataRequests.Add(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.Cancelled)));
        }

        if (ForcedError is { } forcedError)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Fail(
                SecretVaultError.Create(forcedError)));
        }

        if (!_entries.TryGetValue(request.Reference, out var storedScope))
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.NotFound)));
        }

        if (storedScope != request.Scope)
        {
            return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Fail(
                SecretVaultError.Create(SecretVaultErrorCode.AccessDenied)));
        }

        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.FromResult(SecretVaultResult<SecretMetadata>.Succeed(
            new SecretMetadata(
                request.Reference,
                "Test credential",
                SecretKind.Other,
                storedScope,
                SecretVaultPersistenceKind.MemoryOnly,
                now,
                now)));
    }

    public ValueTask<SecretVaultResult<IReadOnlyList<SecretMetadata>>> ListMetadataAsync(
        ListSecretMetadataRequest request,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Dispose()
    {
        LastMaterial?.Dispose();
    }
}

internal sealed class RecordingConnectionProgress : IProgress<ConnectionProgress>
{
    public List<ConnectionProgress> Updates { get; } = [];

    public void Report(ConnectionProgress value) => Updates.Add(value);
}

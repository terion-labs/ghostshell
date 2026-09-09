using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Asura.Application;

namespace Asura.Infrastructure;

/// <summary>
/// Owns short-lived, one-use credential tickets. The helper receives only opaque claim metadata;
/// vault material crosses a current-user pipe after the exact connection and token are verified.
/// </summary>
public sealed class ConnectionCredentialBroker : IConnectionCredentialBroker
{
    private readonly ISecretVault _secretVault;
    private readonly TimeProvider _timeProvider;
    private readonly ConnectionCredentialBrokerOptions _options;
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public ConnectionCredentialBroker(
        ISecretVault secretVault,
        TimeProvider timeProvider,
        ConnectionCredentialBrokerOptions options)
    {
        _secretVault = secretVault ?? throw new ArgumentNullException(nameof(secretVault));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = Validate(options);
    }

    public ValueTask<ConnectionRuntimeResult<TerminalLaunchRequest>> PrepareLaunchAsync(
        ConnectionCredentialBrokerRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.Cancelled)));
        }

        if (!HasValidRequirements(request))
        {
            return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.InvalidProfile)));
        }

        var ticketId = RandomNumberGenerator.GetHexString(16, lowercase: true);
        var access = new ConnectionCredentialBrokerAccess(
            $"asura-credential-{RandomNumberGenerator.GetHexString(16, lowercase: true)}",
            ticketId,
            RandomNumberGenerator.GetHexString(32, lowercase: true),
            request.ConnectionId);
        var ticket = new Ticket(
            access,
            request.Requirements,
            _timeProvider.GetUtcNow() + _options.TicketLifetime);
        if (!_tickets.TryAdd(ticketId, ticket))
        {
            return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.ProcessFailed)));
        }

        ticket.Completion = ServeTicketAsync(ticket);
        try
        {
            var launch = ConnectionCredentialSessionInvocation.CreateHelperLaunch(
                _options.SelfReentry,
                access,
                request,
                _options.ConnectTimeout);
            return ValueTask.FromResult(
                ConnectionRuntimeResult<TerminalLaunchRequest>.Succeed(launch));
        }
        catch (ArgumentException)
        {
            ticket.Cancel();
            return ValueTask.FromResult(ConnectionRuntimeResult<TerminalLaunchRequest>.Fail(
                ConnectionRuntimeError.Create(ConnectionRuntimeErrorCode.InvalidProfile)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var ticket in _tickets.Values)
        {
            ticket.Cancel();
        }

        var completions = _tickets.Values
            .Select(ticket => ticket.Completion)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        try
        {
            await Task.WhenAll(completions).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
    }

    private async Task ServeTicketAsync(Ticket ticket)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdown.Token,
            ticket.Cancellation.Token);
        lifetime.CancelAfter(_options.TicketLifetime);
        try
        {
            await using var pipe = CreateServer(ticket.Access.PipeName);
            for (var attempt = 0; attempt < _options.MaximumInvalidClaims; attempt++)
            {
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                ConnectionCredentialBrokerAccess? claim;
                try
                {
                    claim = await ConnectionCredentialBrokerProtocol.ReadRequestAsync(
                            pipe,
                            ticket.Access.PipeName,
                            lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is EndOfStreamException or IOException)
                {
                    Disconnect(pipe);
                    continue;
                }

                if (!Matches(ticket.Access, claim))
                {
                    try
                    {
                        await ConnectionCredentialBrokerProtocol.WriteFailureAsync(
                                pipe,
                                ConnectionCredentialClaimStatus.Denied,
                                lifetime.Token)
                            .ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                    }
                    finally
                    {
                        // Reuse one server instance so a valid helper cannot connect to a retiring
                        // Unix socket between denied claims.
                        Disconnect(pipe);
                    }

                    continue;
                }

                if (_timeProvider.GetUtcNow() >= ticket.ExpiresAt)
                {
                    await ConnectionCredentialBrokerProtocol.WriteFailureAsync(
                            pipe,
                            ConnectionCredentialClaimStatus.Expired,
                            lifetime.Token)
                        .ConfigureAwait(false);
                    return;
                }

                await ResolveAndWriteAsync(ticket, pipe, lifetime.Token).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        catch (Exception)
        {
            // The broker is a process boundary: unexpected adapter failures remain non-sensitive
            // and fail the helper claim instead of escaping into desktop crash diagnostics.
        }
        finally
        {
            _tickets.TryRemove(ticket.Access.TicketId, out _);
            ticket.Dispose();
        }
    }

    private async Task ResolveAndWriteAsync(
        Ticket ticket,
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var values = new List<(ConnectionSecretRequirement Requirement, SecretMaterial Material)>(
            ticket.Requirements.Count);
        try
        {
            var scope = new SecretScope(SecretScopeKind.Connection, ticket.Access.ConnectionId.Value);
            foreach (var requirement in ticket.Requirements)
            {
                SecretVaultResult<SecretMaterial> result;
                try
                {
                    result = await _secretVault.ResolveAsync(
                            new ResolveSecretRequest(
                                requirement.Reference,
                                scope,
                                new SecretUsePurpose(
                                    requirement.Role == ConnectionSecretRole.EnvironmentVariable
                                        ? SecretUseKind.ConnectionEnvironment
                                        : SecretUseKind.ConnectionAuthentication,
                                    ticket.Access.ConnectionId.Value)),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    await ConnectionCredentialBrokerProtocol.WriteFailureAsync(
                            pipe,
                            ConnectionCredentialClaimStatus.VaultFailure,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (result is SecretVaultResult<SecretMaterial>.Failure failure)
                {
                    await ConnectionCredentialBrokerProtocol.WriteFailureAsync(
                            pipe,
                            MapFailure(failure.Error.Code),
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                values.Add((
                    requirement,
                    ((SecretVaultResult<SecretMaterial>.Success)result).Value));
            }

            await ConnectionCredentialBrokerProtocol.WriteSuccessAsync(
                    pipe,
                    values,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (var (_, material) in values)
            {
                material.Dispose();
            }
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static void Disconnect(NamedPipeServerStream pipe)
    {
        if (!pipe.IsConnected)
        {
            return;
        }

        try
        {
            pipe.Disconnect();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
        }
    }

    private static bool Matches(
        ConnectionCredentialBrokerAccess expected,
        ConnectionCredentialBrokerAccess? actual) =>
        actual is not null
        && string.Equals(expected.TicketId, actual.TicketId, StringComparison.Ordinal)
        && expected.ConnectionId == actual.ConnectionId
        && FixedTimeEquals(expected.Token, actual.Token);

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static bool HasValidRequirements(ConnectionCredentialBrokerRequest request)
    {
        if (Encoding.UTF8.GetByteCount(request.ConnectionId.Value) > 4 * 1024
            || request.Requirements.Count > 256)
        {
            return false;
        }

        var environmentNames = new HashSet<string>(StringComparer.Ordinal);
        var roleCounts = new Dictionary<ConnectionSecretRole, int>();
        foreach (var requirement in request.Requirements)
        {
            roleCounts[requirement.Role] = roleCounts.GetValueOrDefault(requirement.Role) + 1;
            if (requirement.Role == ConnectionSecretRole.EnvironmentVariable
                && (Encoding.UTF8.GetByteCount(requirement.EnvironmentVariableName!) > 4 * 1024
                    || !environmentNames.Add(requirement.EnvironmentVariableName!)))
            {
                return false;
            }
        }

        if (roleCounts.GetValueOrDefault(ConnectionSecretRole.Password) > 1
            || roleCounts.GetValueOrDefault(ConnectionSecretRole.PrivateKey) > 1
            || roleCounts.GetValueOrDefault(ConnectionSecretRole.PrivateKeyPassphrase) > 1)
        {
            return false;
        }

        var passwordCount = roleCounts.GetValueOrDefault(ConnectionSecretRole.Password);
        var keyCount = roleCounts.GetValueOrDefault(ConnectionSecretRole.PrivateKey);
        var passphraseCount = roleCounts.GetValueOrDefault(ConnectionSecretRole.PrivateKeyPassphrase);
        return request.Kind == Asura.Core.ConnectionKind.Ssh
            ? request.Authentication switch
            {
                ConnectionAuthenticationMode.Password =>
                    passwordCount == 1 && keyCount == 0 && passphraseCount == 0,
                ConnectionAuthenticationMode.PrivateKey =>
                    passwordCount == 0 && keyCount == 1 && passphraseCount == 0,
                ConnectionAuthenticationMode.PrivateKeyWithPassphrase =>
                    passwordCount == 0 && keyCount == 1 && passphraseCount == 1,
                ConnectionAuthenticationMode.None or ConnectionAuthenticationMode.SshAgent =>
                    passwordCount == 0 && keyCount == 0 && passphraseCount == 0,
                _ => false,
            }
            : passwordCount == 0 && keyCount == 0 && passphraseCount == 0;
    }

    private static ConnectionCredentialClaimStatus MapFailure(SecretVaultErrorCode code) => code switch
    {
        SecretVaultErrorCode.Cancelled or SecretVaultErrorCode.UserCancelled =>
            ConnectionCredentialClaimStatus.Cancelled,
        SecretVaultErrorCode.InvalidRequest or
            SecretVaultErrorCode.NotFound or
            SecretVaultErrorCode.AccessDenied or
            SecretVaultErrorCode.AuthenticationRequired or
            SecretVaultErrorCode.CorruptEntry => ConnectionCredentialClaimStatus.Denied,
        _ => ConnectionCredentialClaimStatus.VaultFailure,
    };

    private static ConnectionCredentialBrokerOptions Validate(ConnectionCredentialBrokerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.SelfReentry);
        if (options.TicketLifetime <= TimeSpan.Zero
            || options.TicketLifetime > TimeSpan.FromMinutes(5)
            || options.ConnectTimeout <= TimeSpan.Zero
            || options.ConnectTimeout > TimeSpan.FromMinutes(1)
            || options.MaximumInvalidClaims is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        return options;
    }

    private sealed class Ticket : IDisposable
    {
        private readonly object _lifetimeLock = new();
        private bool _disposed;

        public Ticket(
            ConnectionCredentialBrokerAccess access,
            IReadOnlyList<ConnectionSecretRequirement> requirements,
            DateTimeOffset expiresAt)
        {
            Access = access;
            Requirements = Array.AsReadOnly(requirements.ToArray());
            ExpiresAt = expiresAt;
        }

        public ConnectionCredentialBrokerAccess Access { get; }

        public IReadOnlyList<ConnectionSecretRequirement> Requirements { get; }

        public DateTimeOffset ExpiresAt { get; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Completion { get; set; }

        public void Cancel()
        {
            lock (_lifetimeLock)
            {
                if (!_disposed)
                {
                    Cancellation.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Cancellation.Dispose();
            }
        }
    }
}

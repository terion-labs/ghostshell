using System.Collections.Concurrent;
using Asura.Application;
using Asura.Core;

namespace Asura.Agent.Providers;

internal sealed record AiProviderRequestCredential(
    Uri Endpoint,
    string AccessToken,
    string? AccountId);

/// <summary>
/// Resolves a vault-owned OAuth session for one provider request. OpenAI access
/// tokens are refreshed under a per-session lock and written back to the vault;
/// raw tokens never become provider-profile state.
/// </summary>
internal sealed class AiProviderOAuthCredentialSource : IDisposable
{
    private static readonly Uri OpenAiTokenEndpoint =
        new("https://auth.openai.com/oauth/token");
    private static readonly Uri OpenAiCodexEndpoint =
        new("https://chatgpt.com/backend-api/codex/");
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(30);

    private readonly AiProviderOAuthOptions _options;
    private readonly AiProviderOAuthHttp _http;
    private readonly AiProviderOAuthVault _vault;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<SecretRef, SemaphoreSlim> _refreshLocks = [];
    private bool _disposed;

    public AiProviderOAuthCredentialSource(
        ISecretVault vault,
        AiProviderOAuthOptions options,
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = new AiProviderOAuthHttp(handler);
        _vault = new AiProviderOAuthVault(vault);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<AiProviderRequestCredential> ResolveAsync(
        AiProviderProfile profile,
        AiProviderAuthentication.OAuth authentication,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var session = await _vault.ResolveAsync(
            profile.Id,
            authentication.Session,
            cancellationToken).ConfigureAwait(false);
        return profile.Identity switch
        {
            AiProviderKind.OpenAi => await ResolveOpenAiAsync(
                profile,
                authentication.Session,
                session,
                cancellationToken).ConfigureAwait(false),
            AiProviderKind.GitHubCopilot => await ResolveGitHubAsync(
                profile,
                authentication.Session,
                session,
                cancellationToken).ConfigureAwait(false),
            _ => throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.InvalidConfiguration),
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
        foreach (var gate in _refreshLocks.Values)
        {
            gate.Dispose();
        }

        _refreshLocks.Clear();
    }

    private async ValueTask<AiProviderRequestCredential> ResolveOpenAiAsync(
        AiProviderProfile profile,
        SecretRef reference,
        AiProviderOAuthSession session,
        CancellationToken cancellationToken)
    {
        RequireProvider(session, "openai");
        if (IsFresh(session))
        {
            return OpenAiCredential(session);
        }

        var gate = _refreshLocks.GetOrAdd(reference, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = await _vault.ResolveAsync(
                profile.Id,
                reference,
                cancellationToken).ConfigureAwait(false);
            RequireProvider(session, "openai");
            if (!IsFresh(session))
            {
                session = await RefreshOpenAiAsync(session, cancellationToken)
                    .ConfigureAwait(false);
                await _vault.StoreAsync(
                    profile.Id,
                    reference,
                    session,
                    cancellationToken).ConfigureAwait(false);
            }

            return OpenAiCredential(session);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<AiProviderRequestCredential> ResolveGitHubAsync(
        AiProviderProfile profile,
        SecretRef reference,
        AiProviderOAuthSession session,
        CancellationToken cancellationToken)
    {
        RequireProvider(session, "github-copilot");
        if (IsFresh(session))
        {
            return GitHubCredential(session);
        }

        var gate = _refreshLocks.GetOrAdd(reference, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = await _vault.ResolveAsync(
                profile.Id,
                reference,
                cancellationToken).ConfigureAwait(false);
            RequireProvider(session, "github-copilot");
            if (!IsFresh(session))
            {
                if (session.RefreshToken is null)
                {
                    throw AiProviderClientException.Create(
                        AiProviderRuntimeErrorCode.AuthenticationFailed);
                }

                session = await GitHubCopilotOAuth.ExchangeAsync(
                    session.RefreshToken,
                    _http,
                    _timeProvider,
                    cancellationToken).ConfigureAwait(false);
                await _vault.StoreAsync(
                    profile.Id,
                    reference,
                    session,
                    cancellationToken).ConfigureAwait(false);
            }

            return GitHubCredential(session);
        }
        finally
        {
            gate.Release();
        }
    }

    private static AiProviderRequestCredential GitHubCredential(
        AiProviderOAuthSession session) => new(
            AiProviderCatalog.Get(AiProviderKind.GitHubCopilot).DefaultEndpoint,
            session.AccessToken,
            session.AccountId);

    private async ValueTask<AiProviderOAuthSession> RefreshOpenAiAsync(
        AiProviderOAuthSession current,
        CancellationToken cancellationToken)
    {
        if (current.RefreshToken is null)
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.AuthenticationFailed);
        }

        var body = await _http.PostFormAsync(
            OpenAiTokenEndpoint,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = _options.OpenAiClientId,
                ["refresh_token"] = current.RefreshToken,
            },
            cancellationToken).ConfigureAwait(false);
        using var document = AiProviderJson.Parse(body);
        var root = document.RootElement;
        var accessToken = AiProviderJson.RequiredBoundedString(
            root,
            "access_token",
            64 * 1024);
        var refreshToken = AiProviderJson.OptionalBoundedString(
                root,
                "refresh_token",
                64 * 1024)
            ?? current.RefreshToken;
        var idToken = AiProviderJson.OptionalBoundedString(root, "id_token", 64 * 1024);
        var now = _timeProvider.GetUtcNow();
        return new AiProviderOAuthSession(
            AiProviderOAuthSession.CurrentSchemaVersion,
            "openai",
            accessToken,
            refreshToken,
            AiProviderOAuthExpiry.Read(root, now),
            AiProviderOAuthClaims.ExtractAccountId(accessToken, idToken)
                ?? current.AccountId);
    }

    private bool IsFresh(AiProviderOAuthSession session) =>
        session.ExpiresAt > _timeProvider.GetUtcNow() + RefreshSkew;

    private static AiProviderRequestCredential OpenAiCredential(
        AiProviderOAuthSession session) =>
        new(OpenAiCodexEndpoint, session.AccessToken, session.AccountId);

    private static void RequireProvider(
        AiProviderOAuthSession session,
        string expected)
    {
        if (!string.Equals(session.Provider, expected, StringComparison.Ordinal))
        {
            throw AiProviderClientException.Create(
                AiProviderRuntimeErrorCode.CredentialUnavailable);
        }
    }

}

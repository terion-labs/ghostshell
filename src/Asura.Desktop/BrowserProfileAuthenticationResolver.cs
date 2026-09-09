using System.Security.Cryptography;
using System.Text;
using Asura.Application;
using Asura.Core;

namespace Asura.Desktop;

internal sealed class BrowserProfileAuthenticationResolver(ISecretVault vault) :
    IBrowserProfileAuthenticationResolver
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly ISecretVault _vault =
        vault ?? throw new ArgumentNullException(nameof(vault));

    public async ValueTask<BrowserAuthenticationCredentials?> ResolveAsync(
        BrowserProfileBinding profile,
        BrowserAuthenticationChallenge challenge,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(challenge);
        var authentication = profile.Definition.Authentication;
        if (authentication is null || !Matches(authentication, challenge))
        {
            return null;
        }

        SecretVaultResult<SecretMaterial> result;
        try
        {
            result = await _vault.ResolveAsync(
                    new ResolveSecretRequest(
                        authentication.PasswordSecret,
                        new SecretScope(
                            SecretScopeKind.BrowserProfile,
                            profile.Definition.Id.Value),
                        new SecretUsePurpose(
                            SecretUseKind.BrowserProfileAuthentication,
                            profile.Definition.Id.Value)),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }

        if (result is not SecretVaultResult<SecretMaterial>.Success success)
        {
            return null;
        }

        using var material = success.Value;
        if (material.Length > BrowserHttpAuthentication.MaximumPasswordByteLength)
        {
            return null;
        }

        var bytes = new byte[material.Length];
        try
        {
            material.CopyTo(bytes);
            var password = StrictUtf8.GetString(bytes);
            return password.Contains('\0')
                ? null
                : new BrowserAuthenticationCredentials(
                    authentication.Username,
                    password);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool Matches(
        BrowserHttpAuthentication authentication,
        BrowserAuthenticationChallenge challenge)
    {
        if (challenge.IsProxy
            || !Uri.TryCreate(challenge.OriginUrl, UriKind.Absolute, out var origin)
            || !string.Equals(origin.Scheme, authentication.OriginScheme, StringComparison.Ordinal)
            || !string.Equals(origin.Host.TrimEnd('.'), authentication.Host, StringComparison.OrdinalIgnoreCase)
            || origin.Port != (authentication.Port ?? (string.Equals(authentication.OriginScheme, "https", StringComparison.Ordinal) ? 443 : 80))
            || challenge.Port != origin.Port
            || !string.Equals(challenge.RouteIdentity, authentication.RouteIdentity, StringComparison.Ordinal)
            || !string.Equals(
                authentication.Host,
                challenge.Host.Trim().TrimEnd('.'),
                StringComparison.OrdinalIgnoreCase)
            || authentication.Realm is { } realm
                && !string.Equals(realm, challenge.Realm, StringComparison.Ordinal))
        {
            return false;
        }

        var expectedScheme = authentication.Scheme switch
        {
            BrowserAuthenticationScheme.Basic => "basic",
            BrowserAuthenticationScheme.Digest => "digest",
            _ => string.Empty,
        };
        return string.Equals(
            expectedScheme,
            challenge.Scheme,
            StringComparison.OrdinalIgnoreCase);
    }
}

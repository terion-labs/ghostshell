using Asura.Core;

namespace Asura.Application;

/// <summary>
/// Starts bounded interactive authentication flows. Provider profiles retain
/// only the returned vault session reference; raw tokens never cross this API.
/// </summary>
public interface IAiProviderAuthenticationRuntime : IDisposable
{
    AiProviderAuthenticationAvailability GetAvailability(
        AiProviderKind provider,
        AiProviderOAuthFlow flow);

    ValueTask<AiProviderBrowserAuthorization> StartBrowserAsync(
        AiProviderProfileId profileId,
        CancellationToken cancellationToken);

    ValueTask<AiProviderDeviceAuthorization> StartDeviceAsync(
        AiProviderProfileId profileId,
        AiProviderKind provider,
        CancellationToken cancellationToken);
}

public sealed record AiProviderAuthenticationAvailability(
    bool IsAvailable,
    string StableCode,
    string Message)
{
    public static AiProviderAuthenticationAvailability Available { get; } = new(
        true,
        "ai_provider_authentication_available",
        "Interactive authentication is available.");
}

public sealed record AiProviderBrowserAuthorization(
    Uri AuthorizationUri,
    Task<AiProviderAuthenticationResult> Completion);

public sealed record AiProviderDeviceAuthorization(
    Uri VerificationUri,
    string UserCode,
    TimeSpan PollInterval,
    DateTimeOffset ExpiresAt,
    Task<AiProviderAuthenticationResult> Completion);

public sealed record AiProviderAuthenticationResult
{
    private AiProviderAuthenticationResult(
        bool succeeded,
        SecretRef? session,
        string stableCode,
        string message)
    {
        Succeeded = succeeded;
        Session = session;
        StableCode = stableCode;
        Message = message;
    }

    public bool Succeeded { get; }

    public SecretRef? Session { get; }

    public string StableCode { get; }

    public string Message { get; }

    public static AiProviderAuthenticationResult Success(SecretRef session) =>
        new(true, session, "ai_provider_authentication_succeeded", "Connected.");

    public static AiProviderAuthenticationResult Failure(
        string stableCode,
        string message) =>
        new(false, null, stableCode, message);
}

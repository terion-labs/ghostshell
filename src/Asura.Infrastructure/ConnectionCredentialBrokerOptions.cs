namespace Asura.Infrastructure;

public sealed record ConnectionCredentialBrokerOptions
{
    public required SelfReentryLaunch SelfReentry { get; init; }

    public TimeSpan TicketLifetime { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaximumInvalidClaims { get; init; } = 3;
}

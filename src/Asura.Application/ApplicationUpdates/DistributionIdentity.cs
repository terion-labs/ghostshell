namespace Asura.Application.ApplicationUpdates;

public sealed record DistributionIdentity(
    DistributionSource Source,
    ApplicationUpdateStrategy UpdateStrategy,
    string Channel)
{
    public static DistributionIdentity Development { get; } = new(
        DistributionSource.Development,
        ApplicationUpdateStrategy.None,
        "development");
}

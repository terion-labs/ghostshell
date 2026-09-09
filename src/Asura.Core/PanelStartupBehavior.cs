namespace Asura.Core;

public sealed record PanelStartupBehavior
{
    public PanelStartupBehavior(
        string? location = null,
        IReadOnlyList<string>? commands = null,
        StartupCommandDeliveryFailurePolicy deliveryFailurePolicy =
            StartupCommandDeliveryFailurePolicy.RetryWhileLive)
    {
        if (!Enum.IsDefined(deliveryFailurePolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deliveryFailurePolicy),
                deliveryFailurePolicy,
                "The startup-command delivery failure policy is not recognized.");
        }

        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        Commands = Array.AsReadOnly(commands?.ToArray() ?? []);
        DeliveryFailurePolicy = deliveryFailurePolicy;
    }

    public static PanelStartupBehavior None { get; } = new();

    /// <summary>
    /// A kind-specific initial location: working directory, URL, or file-provider path.
    /// </summary>
    public string? Location { get; }

    public IReadOnlyList<string> Commands { get; }

    public StartupCommandDeliveryFailurePolicy DeliveryFailurePolicy { get; }
}

namespace Asura.Core;

public enum StartupCommandDeliveryFailurePolicy
{
    RetryWhileLive,
    StopAfterFirstDeliveryFailure,
}

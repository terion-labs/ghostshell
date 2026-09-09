namespace Asura.Core.Tests;

public sealed class PanelStartupBehaviorTests
{
    [Fact]
    public void Default_policy_retries_while_the_terminal_is_live()
    {
        var startup = new PanelStartupBehavior();

        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.RetryWhileLive,
            startup.DeliveryFailurePolicy);
    }

    [Fact]
    public void Undefined_delivery_failure_policy_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PanelStartupBehavior(
                deliveryFailurePolicy: (StartupCommandDeliveryFailurePolicy)int.MaxValue));
    }

    [Fact]
    public void Commands_are_defensively_copied_and_policy_is_immutable()
    {
        string[] commands = ["git status"];
        var startup = new PanelStartupBehavior(
            commands: commands,
            deliveryFailurePolicy:
                StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);

        commands[0] = "rm -rf ignored";

        Assert.Equal(["git status"], startup.Commands);
        Assert.Equal(
            StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure,
            startup.DeliveryFailurePolicy);
        Assert.Null(
            typeof(PanelStartupBehavior)
                .GetProperty(nameof(PanelStartupBehavior.DeliveryFailurePolicy))!
                .SetMethod);
    }
}

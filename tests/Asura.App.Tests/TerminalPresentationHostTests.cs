using Asura.App.Controls;
using Asura.Application;
using Asura.Core;
using Avalonia.Automation;

namespace Asura.App.Tests;

public sealed class TerminalPresentationHostTests
{
    [Fact]
    public void Default_presentation_exposes_named_live_status_to_accessibility_clients()
    {
        var presentation = new TerminalPresentationHost();
        var managed = Assert.IsType<ManagedTerminalSessionHost>(presentation.Presentation);

        Assert.Equal("Interactive terminal", AutomationProperties.GetName(presentation));
        Assert.Equal(
            AutomationLiveSetting.Polite,
            AutomationProperties.GetLiveSetting(presentation));
        Assert.Equal("Starting", AutomationProperties.GetItemStatus(presentation));
        Assert.Same(managed, presentation.Content);
    }

    [Fact]
    public void Presentation_passes_the_runtime_owned_dispatch_state_to_the_managed_host()
    {
        var panelId = PanelInstanceId.New();
        var state = new TerminalStartupCommandDispatchState(
            panelId,
            ["deploy"],
            OperationContext.ForHuman(
                ClientId.New(),
                idempotencyKey: IdempotencyKey.New()),
            failurePolicy:
                StartupCommandDeliveryFailurePolicy.StopAfterFirstDeliveryFailure);
        var presentation = new TerminalPresentationHost
        {
            StartupCommandDispatchState = state,
        };

        var managed = Assert.IsType<ManagedTerminalSessionHost>(presentation.Presentation);

        Assert.Same(state, managed.StartupCommandDispatchState);
    }

    [Fact]
    public void Presentation_passes_background_opacity_without_fading_the_control_tree()
    {
        var presentation = new TerminalPresentationHost
        {
            BackgroundOpacity = 0.43,
        };

        var managed = Assert.IsType<ManagedTerminalSessionHost>(presentation.Presentation);

        Assert.Equal(0.43, managed.BackgroundOpacity);
        Assert.Equal(0.43, managed.Surface.BackgroundOpacity);
        Assert.Equal(1, presentation.Opacity);
        Assert.Equal(1, managed.Opacity);
    }
}

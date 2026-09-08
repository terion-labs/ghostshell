using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Data;
using GhostShell.App.Controls;

namespace GhostShell.App.Tests;

[Collection(AvaloniaUiCollection.Name)]
public sealed class LiveRegionTextBlockTests
{
    [Theory]
    [InlineData(AutomationLiveSetting.Polite)]
    [InlineData(AutomationLiveSetting.Assertive)]
    public void Full_text_is_announced_and_clear_keeps_a_valid_name_without_an_empty_announcement(
        AutomationLiveSetting setting)
    {
        var text = new string('x', 10000) + " · completed";
        var control = new LiveRegionTextBlock { Text = text };
        AutomationProperties.SetName(control, "Browser status");
        AutomationProperties.SetLiveSetting(control, setting);
        var peer = ControlAutomationPeer.CreatePeerForElement(control)!;

        Assert.Equal(text, peer.GetName());
        Assert.Equal(setting, peer.GetLiveSetting());

        control.Text = string.Empty;
        Assert.Equal("Browser status", peer.GetName());
        Assert.Equal(AutomationLiveSetting.Off, peer.GetLiveSetting());

        control.Text = "Ready again";
        Assert.Equal("Ready again", peer.GetName());
        Assert.Equal(setting, peer.GetLiveSetting());
    }

    [Fact]
    public void Empty_or_detached_bindings_remain_accessible_without_announcing_a_clear()
    {
        var control = new LiveRegionTextBlock();
        AutomationProperties.SetLiveSetting(control, AutomationLiveSetting.Polite);
        var peer = ControlAutomationPeer.CreatePeerForElement(control)!;
        Assert.Equal("Status", peer.GetName());
        Assert.Equal(AutomationLiveSetting.Off, peer.GetLiveSetting());

        control.Bind(Avalonia.Controls.TextBlock.TextProperty, new Binding("Message"));
        control.DataContext = new StatusContext("Loading complete");
        Assert.Equal("Loading complete", peer.GetName());
        Assert.Equal(AutomationLiveSetting.Polite, peer.GetLiveSetting());
        control.DataContext = null;
        Assert.Equal("Status", peer.GetName());
        Assert.Equal(AutomationLiveSetting.Off, peer.GetLiveSetting());
    }

    private sealed record StatusContext(string Message);
}

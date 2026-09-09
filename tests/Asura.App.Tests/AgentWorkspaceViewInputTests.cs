using Asura.App.Views;
using Avalonia.Input;

namespace Asura.App.Tests;

public sealed class AgentWorkspaceViewInputTests
{
    [Fact]
    public void Enter_submits_while_shift_enter_remains_a_newline()
    {
        Assert.True(AgentWorkspaceView.ShouldSubmitPrompt(
            Key.Enter,
            KeyModifiers.None));
        Assert.False(AgentWorkspaceView.ShouldSubmitPrompt(
            Key.Enter,
            KeyModifiers.Shift));
        Assert.False(AgentWorkspaceView.ShouldSubmitPrompt(
            Key.Enter,
            KeyModifiers.Control));
    }

    [Fact]
    public void Super_enter_requests_steering_without_submitting_normally()
    {
        Assert.True(AgentWorkspaceView.ShouldQueueSteering(
            Key.Enter,
            KeyModifiers.Meta));
        Assert.False(AgentWorkspaceView.ShouldSubmitPrompt(
            Key.Enter,
            KeyModifiers.Meta));
        Assert.False(AgentWorkspaceView.ShouldQueueSteering(
            Key.Enter,
            KeyModifiers.Shift));
    }
}

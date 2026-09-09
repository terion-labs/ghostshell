using Asura.App.ViewModels;

namespace Asura.App.Tests;

/// <summary>
/// With no provider configured the panel used to show a provider picker with
/// nothing in it, a scope picker, and a capability card describing a run that
/// cannot happen — all stacked above an empty state explaining how to get a
/// provider. Nothing there was usable, and four idioms competed for one space.
/// </summary>
public sealed partial class AgentChatViewModelTests
{
    [Fact]
    public void No_provider_leaves_only_the_empty_state()
    {
        using var runtime = new StubGovernedRuntime();
        using var profiles = new StubProfileRuntime();

        using var viewModel = new AgentChatViewModel(
            runtime,
            profiles,
            ImmediateUiThreadDispatcher.Instance);

        Assert.False(viewModel.HasProvider);

        // The card describes what a run would be allowed to do. There is no run.
        Assert.False(viewModel.HasCapabilityNotice);
    }
}

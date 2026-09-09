using Asura.Core;

namespace Asura.Application.Tests;

public sealed class BrowserAutomationContractsTests
{
    [Fact]
    public void BindingMatchesOnlyTheExactDocumentViewportAndInputEpoch()
    {
        var state = State(inputEpoch: 4);
        var binding = BrowserAutomationBinding.FromState(state);

        Assert.True(binding.Matches(state));
        Assert.False(binding.Matches(State(inputEpoch: 5)));
        Assert.False(binding.Matches(new BrowserSessionState(
            state.Address,
            state.Title,
            state.LoadState,
            state.CanGoBack,
            state.CanGoForward,
            state.DocumentRevision,
            viewport: new BrowserViewportState(801, 600, 1),
            viewportRevision: state.ViewportRevision + 1,
            inputEpoch: state.InputEpoch)));
    }

    [Fact]
    public void MouseAndScrollAreBoundedToTheObservedCssViewport()
    {
        var binding = BrowserAutomationBinding.FromState(State());
        var mouse = new BrowserMouseRequest(
            new SessionId("browser"),
            binding,
            BrowserMouseAction.Click,
            799,
            599,
            BrowserMouseButton.Left,
            clickCount: 1);

        Assert.Equal(799, mouse.XCss);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserMouseRequest(
                new SessionId("browser"),
                binding,
                BrowserMouseAction.Move,
                800,
                10));
        Assert.Throws<ArgumentException>(() =>
            new BrowserMouseRequest(
                new SessionId("browser"),
                binding,
                BrowserMouseAction.Click,
                10,
                10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserScrollRequest(
                new SessionId("browser"),
                binding,
                10,
                10,
                0,
                BrowserScrollRequest.MaximumDelta + 1));
    }

    [Theory]
    [InlineData("document.cookie")]
    [InlineData("window['localStorage']")]
    [InlineData("fetch('/', {headers: {Authorization: 'x'}})")]
    [InlineData("setTimeout(() => 1, 1)")]
    [InlineData("const private_key = 'x'")]
    public void EvaluateRejectsSecretAndDurableSideEffectPatterns(string source)
    {
        Assert.Throws<ArgumentException>(() =>
            new BrowserEvaluateRequest(
                new SessionId("browser"),
                BrowserAutomationBinding.FromState(State()),
                source));
    }

    [Fact]
    public void EvaluateRejectsInvalidUnicodeAndUnboundedTimeouts()
    {
        var binding = BrowserAutomationBinding.FromState(State());
        Assert.Throws<ArgumentException>(() =>
            new BrowserEvaluateRequest(
                new SessionId("browser"),
                binding,
                "'\ud800'"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserEvaluateRequest(
                new SessionId("browser"),
                binding,
                "1",
                timeout: TimeSpan.FromSeconds(31)));
    }

    [Theory]
    [InlineData("{\"password\":\"value\"}")]
    [InlineData("{\"nested\":{\"apiToken\":\"value\"}}")]
    [InlineData("\"Authorization: Bearer value\"")]
    public void EvaluationResultRejectsSecretBearingJson(string json)
    {
        var state = State();
        Assert.Throws<ArgumentException>(() =>
            new BrowserEvaluationResult(
                BrowserAutomationBinding.FromState(state),
                state,
                json));
    }

    [Fact]
    public void EvaluationResultEnforcesItsWrapperSafeUtf8Budget()
    {
        var state = State();
        var binding = BrowserAutomationBinding.FromState(state);
        var maximumString = "\"" + new string(
            'x',
            BrowserEvaluationResult.MaximumJsonBytes - 2) + "\"";

        var accepted = new BrowserEvaluationResult(binding, state, maximumString);

        Assert.Equal(BrowserEvaluationResult.MaximumJsonBytes, maximumString.Length);
        Assert.Equal(maximumString, accepted.Json);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserEvaluationResult(binding, state, maximumString + " "));
    }

    [Fact]
    public void CatalogClassifiesInputsAsMutationsAndEvaluationAsPrivileged()
    {
        foreach (var toolName in new[]
                 {
                     BuiltInAgentTools.BrowserMouse,
                     BuiltInAgentTools.BrowserKey,
                     BuiltInAgentTools.BrowserScroll,
                 })
        {
            Assert.True(BuiltInAgentTools.Catalog.TryGet(toolName, out var tool));
            Assert.Equal(AgentCapability.BrowserInteraction, tool!.Capability);
            Assert.Equal(AgentActionRisk.Mutation, tool.Risk);
        }

        Assert.True(BuiltInAgentTools.Catalog.TryGet(
            BuiltInAgentTools.BrowserEvaluate,
            out var evaluate));
        Assert.Equal(AgentCapability.BrowserScripting, evaluate!.Capability);
        Assert.Equal(AgentActionRisk.Privileged, evaluate.Risk);
    }

    private static BrowserSessionState State(long inputEpoch = 4) =>
        new(
            new BrowserAddress(new Uri("https://example.test/page")),
            "Example",
            BrowserLoadState.Ready,
            canGoBack: false,
            canGoForward: false,
            documentRevision: 7,
            viewport: new BrowserViewportState(800, 600, 1),
            viewportRevision: 3,
            inputEpoch: inputEpoch);
}
